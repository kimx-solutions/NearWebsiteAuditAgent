# 資料流程與安全邊界

直接 API（2026-10-29）：獨立 host 只依賴 Contracts／Core／Infrastructure，API key 在入口驗證，不傳入 target transport。以 framework fixed-window limiter＋concurrency limiter 控制所有已認證呼叫，queue=0；body 有 8192-byte／10 秒限制，再呼叫既有 orchestrator。結果只於記憶體形成有界 JSON，不碰市場／journal／report store。錯誤與 request log 不回顯 body、query、Authorization 或例外內容。完整契約見 [直接 API](api-contract.md#direct-audit-api)。

本文件擁有 transport、資料流與資源預算。需求來源為 R01、R05、R06、R08；D02／D04 保守提案已隨開工採用，具體防護與操作上限見下方及 operations。DTO 見 [API](api-contract.md#local-contract)，檢查與狀態見 [domain](domain-spec.md)。

## Entry to output

```text
Local JSON file                 NEAR poll response (authenticated)
      |                                  |
      |                      assignment/revision + strict brief adapter
      +--------------------+-------------+
                           v
                  Contracts.AuditRequest
                           |
            validate input / policy / budget / deadline
                           |
                Core.AuditOrchestrator
                    |               |
          safe target transport     assignment repository (market only)
                    |               |
        DNS -> vetted IP -> socket  durable state + pending submission
                    |
      HTTP/TLS observations -> AngleSharp (no network, no scripts)
                    |
       deterministic checkers -> scoring -> AuditResult
                    |
       redacted JSON + escaped Markdown -> atomic report files
                                         |
                          trusted NEAR client -> submit / reconcile
```

依賴方向提案：Core 只依賴 Contracts；Infrastructure 實作 Core ports 並依賴 Contracts；Worker 組裝三者。Tests 依所測層引用；不得讓 Core 引用 Worker／Infrastructure 或需要 NEAR 才能執行本地 audit。

## Network boundary

1. 使用單一 URI parser 處理初始 URL、redirect、HTML links；僅絕對 HTTP(S)、無 userinfo、無控制字元／IPv6 zone ID。以 IDN 正規化 host，移除 fragment；path／query 保留供實際請求，但不能流入 log／公開報告。只允許 port 80 或 443，不自動掃描其他 port。
2. DNS 前拒絕已知本機／metadata host 名，IP literal 也須同一分類器。分類器涵蓋 IPv4／IPv6 loopback、unspecified、private／ULA、link-local、multicast、CGNAT、保留／特殊用途／非全球可路由範圍與 metadata endpoints；IPv4-mapped IPv6 先還原分類，拒絕可繞過分類的 transition／NAT64 地址。不能只檢查 `IsLoopback` 或 RFC1918。
3. 每次新連線解析完整 A／AAAA；任何結果受限就拒絕整個目的地；零結果為 `dns_failed`。限定 DNS 時間，解析輸出有界；失敗不能當公開地址。
4. `SocketsHttpHandler.ConnectCallback` 類 port 將「已驗證 IP」交給 socket，不能檢查完 DNS 再交 hostname 讓系統重解。比對 connected RemoteEndPoint 與核准 IP；不符即關閉且不送 HTTP bytes。新連線／重試均重驗，不跨任務共用未驗證 connection。SNI／Host 與 certificate hostname validation 使用原 host，不能被 IP 替換破壞。
5. 關閉 auto redirect、proxy（含系統與環境 proxy）、credentials、automatic cookie jar；不同目標間無共享 cookie／authorization。固定 HTTP/1.1 以限制 Alt-Svc／HTTP3 等替代連線路徑，壓縮解碼與 header 讀取有上限。
6. 手動處理 301/302/303/307/308 的 Location；相對 URL 依目前 URL 解決，每一跳重新執行 1–4。允許跨公開 origin 的主頁 redirect，但 HTTPS 降級 HTTP 立即停止並回報；loop 用標準化 URI（去 fragment）判定，達上限即停止，無 Location 的 redirect 為不可完成的鏈。
7. certificate／TLS probe 使用相同驗證 socket，不允許 diagnostic `SslStream` 直連任意 host。記錄 certificate／policy errors 後仍拒絕無效憑證；不採 trust-all callback，也不在驗證失敗後用不安全 client 取頁面。
8. 憑證 chain 驗證不得透過 AIA／CRL／OCSP 自動對未受管 URL 發送額外請求；提案採關閉 certificate downloads 的離線 OS trust chain，保留 hostname／有效期驗證，明示未做線上 revocation。需在 Windows／Linux 驗證所有隱含 fetch 路徑；未證實不接 production 網路。握手只觀察一次正常協商結果，不做弱協定／cipher sweep。
9. `AngleSharp` 只解析有界 HTML string；不建立可抓外部資源的 browsing context、不執行 JS、不 follow canonical、不讀 attachments。canonical／robots 所含任意 URL 不自動變成新抓取目標。

此設計推導依據：[OWASP SSRF 指引](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html) 的逐目標驗證與 redirect 控制；[Microsoft ConnectCallback](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0) 提供自訂建立連線的入口。是否完整防護仍須 AC03 的實際 socket 與平台測試，讀過文件不等同驗證。

## Probe scope

- 只解析最後主頁的一份 HTML。robots.txt 與 sitemap.xml 只取最後主頁 origin 的根目錄標準路徑；不遞迴 sitemap、不解析其 URL 下載頁面。
- link probes 選最後主頁中同 origin 的 HTTP(S) `<a href>`，移除 fragment 後去重，以 normalized absolute URI ordinal 排序後取前 N；排除 mailto／javascript／data 等。其他 origin 標記為 excluded，不跟隨到該 origin。
- link redirect 同樣必須符合安全政策及同 origin；若跳出則該 link 結果 NotTested／`out_of_scope_redirect`。HEAD 遇 405/501 可做一次 bounded GET；其他狀態不自動改 method，禁止 POST／PUT／DELETE。
- 初始 HTTP 目標的主頁鏈可兼作 HTTP→HTTPS 檢查；初始 HTTPS 目標可對相同 host/path/query 的 HTTP:80 做一次受限升級檢查。無限 redirect 或多 hostname 試探不允許。每個 HTTP attempt／TLS probe 都消耗預算。
- 主頁 HTML、相關檔案、HEAD／GET probes 的 redirect 與 fallback 共用總預算；不因 checker 各自 new HttpClient 取得額外額度。

## Resource budgets

下表為 D02 已隨開工採用的工程預設，不代表 Issue 已指定固定數值。所有 key 支援 .NET environment `__` 階層格式；讀取後啟動時驗證，負數／零／溢位／違反關係視為設定錯誤，不悄悄關閉限制。單 assignment 不得覆蓋維運者的上限。

| Key | 建議值 | 語義 |
| --- | --- | --- |
| `Audit:MaxDurationSeconds` | 60 | 每筆 audit wall-clock 上限，含 DNS／TLS／HTTP／parse，不含獨立提交餘裕 |
| `Audit:RequestTimeoutSeconds` | 10 | 單一 HTTP request，含 headers 與 bounded body |
| `Audit:DnsTimeoutSeconds` / `ConnectTimeoutSeconds` | 3 / 5 | 各段上限，且不超過剩餘 request／audit deadline |
| `Audit:MaxRedirects` | 5 | 每條鏈最多跟隨 5 個跳轉 |
| `Audit:MaxTargetAttempts` | 40 | 每個 HTTP attempt 或獨立 TLS probe 開始前消耗一次；不能 reset |
| `Audit:MaxLinks` | 10 | 選定 link 的上限；未抽樣數量揭露於 scope |
| `Audit:MaxHtmlPages` | 1 | 只解析主頁，不把 probes 轉成爬站 |
| `Audit:MaxResponseBytes` | 2097152 | 每個 response 解碼後最多 2 MiB，含 chunked／未知長度；超限不解析不完整 body |
| `Audit:MaxTotalResponseBytes` | 8388608 | 每筆解碼後 body 總量最多 8 MiB |
| `Audit:MaxResponseHeaderBytes` | 32768 | 每個 response header 最多 32 KiB；transport 與 parser 都須有界 |
| `Audit:MaxConcurrentAudits` / `MaxConcurrentRequestsPerAudit` | 1 / 2 | 兩層 semaphore；取消與錯誤不得洩漏 permit |
| `Audit:MaxReportBytes` | 196608 | 每份 JSON／Markdown 最多 192 KiB；限制細節證據長度，保留 summary／NotTested，不截斷 UTF-8 字元 |
| `Market:PollIntervalSeconds` | 60 | 有界單一輪詢 loop；本地 capacity 滿時不排無限 queue |
| `Market:RequestTimeoutSeconds` | 10 | 平台呼叫單次上限，不與 target timeout 混用 |
| `Market:MaxReadAttempts` / `MaxStartAttempts` | 3 / 3 | 含首次；總 deadline 仍優先，非所有 POST 通用 retry |
| `Market:SubmissionReserveSeconds` | 15 | 不足此餘裕時不啟動 audit；平台 deadline 是上限，餘裕並非交付保證 |
| `Storage:Root` / `MaxBytes` | operator 必填 / 104857600 | 固定根目錄／100 MiB 容量提案；新工作前預留報告與 journal 所需空間 |
| `Storage:RetentionDays` | 7 | 只清理已對帳完成且超過期限的資料；不刪未知提交或執行中任務 |

發現外部資料不適合以上預算時明列 Partial／NotTested 與原因，不自動放大。CPU parsing／排序也須定期檢查 cancellation；對大型或惡意 HTML、壓縮炸彈、超長 header、slow response 均有驗證路徑。

`Market:BaseUrl` 提案固定 `https://market.near.ai`；token 由 `Market:AgentApiToken` 機密環境變數提供。測試 host 只能由維運者設定並核准，不得從 assignment input 取得；沒有已確認的官方 sandbox URL。

## Persistence and secrets

市場 client 與 target client 完全分離。平台 Bearer 只送往核准平台 origin，拒絕帶憑證跟 redirect；target request 不帶平台標頭、預設登入資訊、cookie jar、買方 credentials。brief 不能定義 headers、proxy、callback URL、輸出路徑或執行命令。

raw body／Set-Cookie 在記憶體完成判讀後丟棄；certificate 僅保存公開摘要與錯誤碼。cookie values 不得進 findings、exceptions、trace 或 test failure logs。query values、URL userinfo、敏感 header、完整 brief 也不可進 report／log。保留完整目標 URL 供重啟僅限受保護 journal，其存取權等同機密資料；debug logging 不能旁路 redaction。

固定根目錄下由系統產生 GUID／hash file names，不使用 user URL 或外部 ID 當路徑；拒 traversal、symlink／reparse point 逃逸。資料夾以服務帳號最低權限限制。寫 temporary file、flush、atomic rename 後才轉狀態；同一根目錄持有 process lock。journal 含任務 revision／狀態／safe error／報告 hash／relative paths，實際 schema 見 [domain repositories](domain-spec.md#repositories)。

report 寫入失敗為該任務失敗，不可先標 Submitted。容量不足／磁碟不可寫暫停新市場任務，保留恢復所需檔案。不得以無持久化的 production path 規避恢復驗收。

## Implementation safeguards

公開 report URL 僅顯示 origin；resource 用 path/query 的短 hash 識別。完整 URL 只留 protected journal。DOM 在 parse 前有 50,000 markup guard；超限保留其他觀測，HTML checks 不可測。可調預算另有保守硬上限，設定 key／數值關係見 operations，不允許任務覆蓋。

Market:TestAssignmentIds 是明確測試 allowlist，Market:AcceptPaidAssignments 預設 false。只有成功測試 audit（至少一個可測 check）、submit 並讀回 submitted/accepted，才記 market-validation.json（Agent ID、test assignment ID、UTC time）。同一 Agent 的該 metadata 加正式接單開關，才允許處理其他已分配任務。這是 D05 的技術保護，不能取代尚未執行的真實平台驗收。

驗證 metadata 長期保留但不含 token／URL／brief；完成且已對帳的 report/journal 才依 7 天政策清理，unknown／NeedsReview／orphan 不清理。入口目前序列執行，實際並行 1；每筆 transport semaphore 仍執行設定上限。

實作容量 gate：單 instance lock 與循序 market worker 下，start／audit 前取得容量 lease，檢查兩份最大 report＋64 KiB journal／metadata 及 8 slots；ReportReady 為 64 KiB／4 slots。cycle 先做安全 retention，再接新工作。配額不足不 start；實體磁碟或 OS I/O 失敗仍停入口並保留 journal。poll timeout 僅限 list request，不傳入已開始的 assignment；audit 維持自身 duration 與 SLA 餘裕限制。
