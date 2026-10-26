# 操作與部署

## 環境與 Local

SDK 10.0.403、最低 runtime 10.0.14（允許更新 patch），來源為 [Microsoft .NET 10 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)，查核日 2026-10-26。本地 .tools 的兩 OS SDK 已驗證 SHA512，不修改系統安裝；其他環境需自行安裝 pinned SDK。部署已發布程式只需對應 runtime。

在 repo root 建立只含 url 的 UTF-8 JSON file。拒絕 duplicate／unknown keys、多 URL、headers、proxy、credentials 與政策 override；輸入最多 8192 bytes。

~~~powershell
dotnet run --project src/NearWebsiteAuditAgent.Worker -- --Execution:Mode Local --Execution:InputFile request.json --Storage:Root .audit-data/local
~~~

Mode、Storage:Root 必填；Local 另需 InputFile，無需 token。相對 path 依 process cwd 解決，服務部署使用絕對 path。設定錯誤 exit 1，audit exit codes 見 README。

JSON/Markdown 是同一 AuditResult，含 rules／score version、coverage、limits 與 errors。URL 只顯示 origin，resource evidence 用 hash 辨識。fixture sample 不是公網站點實際掃描或 NEAR 真實交付。

## 設定與預算

非機密設定支援 appsettings、CLI、.NET environment 階層 key，例如 Audit__MaxLinks=5。預設與語義見 [data-flow](website-audit-mvp/data-flow.md#resource-budgets)，規則／評分見 [domain](website-audit-mvp/domain-spec.md#scoring)。

硬上限：duration≤300 秒、attempts≤200、links≤50、HTML pages=1、decoded response≤8 MiB、總 body≤32 MiB、headers≤128 KiB、report≤192 KiB、各 concurrency≤8。入口依序執行（實際並行 1，不超過設定），transport 共用每筆 semaphore。DNS/connect timeout≤request≤audit duration。

DOM 配置前最多接受 50,000 個 markup 開頭；超限保留 HTTP/TLS 結果，DOM checks 不可測。相同 rule/check/severity 的 findings 合併，保留三份 evidence 與省略次數，不改變 score。所有 probes、fallback、redirect 共用預算。Cookie check evidence 只含名稱／旗標／persistent、nonpersistent 或 unknown 分類；期限語法不明保留 unknown，不推測登入用途。sitemap parser 上限配合 response 的 8 MiB 硬上限，未取得完整資源或超過 parser 限制時不扣 invalid XML 分數。輪詢 deadline 與單 assignment 的 audit／SLA deadline 分開，poll interval 不會縮短已啟動稽核的時間。

Storage 須容納兩份最大 report 加 64 KiB journal，最多 4096 files。每次市場任務 start 前，在單 instance lock／循序執行下取得容量 lease，預留兩份最大 report、64 KiB journal／metadata 及 8 個 file slots；ReportReady 只留 64 KiB／4 slots。容量不足時不 start，清理完成且已對帳的舊資料後可重啟續跑。此為程式配額保護，其他程式耗盡實體磁碟或 OS I/O 錯誤仍會安全停止。Market page size=50、每頁 overlap 一筆，最多 MaxPages（預設 20）；超限回明確錯誤，不假裝零筆。每份平台 JSON 最多 4 MiB，不下載 brief 附件。

## NEAR token 與測試

依 [官方 self-hosted guide](https://docs.market.near.ai/guides/self-hosted-agents) 建立 agent／token。程式只處理已分配工作，不建立 listing、不投標、不發買方訊息、不接受／移轉資金。base URL 固定 https://market.near.ai，不猜 sandbox endpoint。

token 只讀 process environment 的 Market__AgentApiToken 或 AGENT_API_TOKEN；CLI 的 AgentApiToken 會被拒絕，同名 settings file 值也不採用。不要把真實 token 放進 chat、git、log、request file 或 shell command literal。

PowerShell foreground 測試可用不進 history 的輸入：

~~~powershell
$tokenValue = Read-Host "Agent API token" -AsSecureString
$env:Market__AgentApiToken = [System.Net.NetworkCredential]::new("", $tokenValue).Password
$env:Market__TestAssignmentIds__0 = "替換成已明確授權的測試-assignment-UUID"
dotnet run --project src/NearWebsiteAuditAgent.Worker -- --Execution:Mode Market --Storage:Root .audit-data/market
Remove-Item Env:Market__AgentApiToken
~~~

UUID 是待替換說明，不是假任務；先確認官方測試方式及可能費用。官方 draft self-hire 不等於免費 sandbox。本次沒有建立／操作真實任務。

test allowlist 中的 assignment brief 必須是 local JSON 格式。audit、submit 成功並讀回 submitted/accepted 後，寫 market-validation.json：僅 Agent ID／test assignment ID／time，無 token、URL 或 brief。維運者仍需確認平台可見結果與 AC16 證據。

只有同一 Agent 的驗證 metadata 存在且 Market__AcceptPaidAssignments=true，才執行 allowlist 以外的已分配工作；預設 false。本地 fake client 測試不構成真實市場驗收。

## Journal 與恢復

同一 Storage:Root 只能一個 process。Windows root 使用 current service user 專屬、不繼承的 ACL；Linux directory=0700、new file=0600。禁止共享、非可信使用者可修改、symlink/reparse point 的 root；完整 target URL 只存受保護 journal。

- Starting／Running：依 journal 恢復並重驗 DNS／SLA；不因 startedAt 非空而漏單。沒有 journal 的已 start 任務停 NeedsReview。
- ReportReady：核對 Markdown hash、遠端 revision，再提交；不重跑 audit。
- Submitting／AwaitingReconciliation：先讀平台；已提交不重送，不明則保留。沒有已確認的 submit idempotency key。
- NeedsReview：查明 brief、ownership、revision、storage／contract；不要刪 journal 來強迫重送。維運者可在平台人工處理，再對帳。
- Request changes：需新的 SLA deadline、既有 submitted history、完整 buyer feedback 且符合相同 JSON 格式；最多一次自動 revision。一般訊息／truncated feedback 不重跑。
- 401／403／不相容契約：停止市場入口，更新受管憑證／契約後重啟。單一 target 的網路／HTML 錯誤不使後續 audit crash。
- 容量／ACL／journal 損毀：停止新工作，保留並備份資料，先核對平台再修復。不開啟 request/body debug dump。

Market cycle 在輪詢前與完成後清理超過 RetentionDays（預設 7）的完成且已對帳 report／journal；Local 報告由維運者管理。Running、unknown submission、NeedsReview、無完成 journal 的 orphan 不自動刪除。最小測試驗證 metadata 長期保留，不含網站／憑證資料。

## Publish 與服務

~~~powershell
dotnet publish src/NearWebsiteAuditAgent.Worker -c Release -r win-x64 --self-contained false -o publish/win-x64
dotnet publish src/NearWebsiteAuditAgent.Worker -c Release -r linux-x64 --self-contained false -o publish/linux-x64
~~~

Windows console 可用發布後 exe 加同樣設定。已接 Windows Service lifetime；SCM 註冊、帳號、ACL、受保護 environment injection 由維運者部署，本次未註冊／啟停系統服務。token 不放 service binPath 或全機公開環境變數。

Linux systemd 範本（本次未安裝）：

~~~ini
[Unit]
Description=NEAR Website Audit Agent
After=network-online.target

[Service]
Type=simple
User=nearaudit
WorkingDirectory=/opt/near-audit
ExecStart=/opt/near-audit/NearWebsiteAuditAgent.Worker --Execution:Mode Market --Storage:Root /var/lib/near-audit
EnvironmentFile=/etc/near-audit/agent.env
Restart=no
TimeoutStopSec=30
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ReadWritePaths=/var/lib/near-audit

[Install]
WantedBy=multi-user.target
~~~

管理者建立專屬帳號、runtime、publish 目錄與 root-owned 0600 EnvironmentFile，注入 token／test IDs／開關。SIGTERM／cancellation 會傳到網路／audit，提交不明時保存對帳狀態。可搭配服務帳號的 egress firewall。

## 驗證

~~~powershell
dotnet restore NearWebsiteAuditAgent.slnx --locked-mode
dotnet build NearWebsiteAuditAgent.slnx -c Release --no-restore
dotnet test tests/NearWebsiteAuditAgent.Tests/NearWebsiteAuditAgent.Tests.csproj -c Release --no-build
dotnet format NearWebsiteAuditAgent.slnx whitespace --verify-no-changes --no-restore
dotnet list NearWebsiteAuditAgent.slnx package --vulnerable --include-transitive
~~~

Linux tests 需 openssl；Windows 需 Git for Windows 的 Program Files/Git/usr/bin/openssl.exe。fixture 僅啟動隱藏的自有 child server，完成後清理，用以隔離 Schannel server 自行抓 intermediate 的行為，驗證 client 的 AIA egress 邊界。

CI 提供 Windows／Ubuntu matrix，actions 綁定官方 release 對應 commit；新增 workflow 不代表遠端 CI 已跑。兩 OS／CLI 結果、scope、真實 NEAR 與系統服務部署缺口見 owning slice evidence／plan。
