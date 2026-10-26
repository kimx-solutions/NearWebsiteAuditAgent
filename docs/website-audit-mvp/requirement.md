---
title: Website Audit Agent MVP 需求
status: approved-for-implementation
module_code: website-audit
slice_id: website-audit-mvp
module_spec_package: docs/website-audit-mvp
req_anchor: https://github.com/kimx-solutions/NearWebsiteAuditAgent/issues/1
eng_anchor: docs/website-audit-mvp/implementation-plan.md
---

# Website Audit Agent MVP

## 狀態與來源

本包原由需求正規化草案起步，使用者已於 2026-10-26 以「開始實作」批准本包保守方案。Issue 仍是產品需求來源；未假設其他專案的 REQ freeze。D05 的真實市場驗證仍待維運者輸入與授權，不能把開工當成驗收完成。

| ID | 類別 | 來源與版本 | 效力 |
| --- | --- | --- | --- |
| S1 | 需求來源 | [Issue #1](https://github.com/kimx-solutions/NearWebsiteAuditAgent/issues/1)，`updated_at=2026-10-26T08:09:58Z`；2026-10-26 讀取，open、無留言 | 下列 R01–R09 的產品意圖；Issue 變動時重做影響分析 |
| S2 | 使用者決策 | 本對話 2026-10-26 回覆 `docs` | 文件 workspace=`docs/`；沿用建議 slice 名 `website-audit-mvp`，只替換 workspace |
| S3 | as-built | `main` / `23f1e1cfe1d2c34d0f49dccc045a538c4b50c68d`；規格撰寫前工作樹乾淨，僅 `.gitignore` | 沒有 solution、程式、測試、README、AGENTS、profile、Notes 或既有 slice 可沿用 |
| S4 | 外部契約 | [NEAR self-hosted guide](https://docs.market.near.ai/guides/self-hosted-agents)、[listing guide](https://docs.market.near.ai/list-your-agent)、[Worker tools](https://docs.market.near.ai/mcp/worker-tools)，查核日 2026-10-26 | 可用的官方整合語義；精確 HTTP 來源與查核界線見 [API](api-contract.md#near-api) |
| S5 | 技術依據 | [ConnectCallback](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback?view=net-10.0)、[OWASP SSRF](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html)，查核日 2026-10-26 | 支持安全設計推導，不是既有功能或業務批准 |

## 目標與角色

讓維運者自架一個 .NET 10 / C# Worker，對公開網站做確定性、非侵入式安全與基本 SEO 檢查，輸出可機讀 JSON 與可交付 Markdown，並處理已分配的 NEAR 任務。

- 維運者：設定憑證、預算、儲存目錄與執行模式，處理不可自動恢復的任務。
- 委託人：經 NEAR 提供目標網站並接收報告。
- NEAR Agent Market：任務與 SLA 狀態的外部權威。
- 受查網站：不可信資料來源；不能變更 Agent 設定、網路政策或取得平台憑證。

## 範圍與需求錨點

| ID | S1 來源 anchor | 行為 | 所屬契約／驗收 |
| --- | --- | --- | --- |
| R01 | Phase 1 / Project Foundation；AC 6、9 | Worker、Core、Infrastructure、Contracts、Tests；DI、設定、結構化 log、取消、timeout、orchestrator | [Domain](domain-spec.md#model)；AC01、AC13、AC17 |
| R02 | Phase 2 / HTTPS、Headers、Cookies、Redirects；AC 2 | HTTPS、憑證與協商 TLS；安全 headers；cookie attributes；HTTP 升級與 redirect loop/chain | [檢查目錄](domain-spec.md#checks)；AC04–AC07 |
| R03 | Phase 3；AC 2 | title、description、H1、canonical、robots meta、robots.txt、sitemap.xml、限量斷鏈檢查 | [檢查目錄](domain-spec.md#checks)；AC08–AC09 |
| R04 | Phase 4；AC 1、3、4 | JSON、Markdown、五級 severity、證據與建議、確定性 0–100 評分；不可測為 NotTested | [報告](api-contract.md#report)、[評分](domain-spec.md#scoring)；AC01、AC10–AC11 |
| R05 | Phase 5；AC 7 | 官方 API、agent token、取得任務、驗證輸入、開始、執行、提交、狀態追蹤、有限 retry／冪等／SLA；先通過測試任務 | [NEAR API](api-contract.md#near-api)、[工作狀態](domain-spec.md#assignment-state)；AC14–AC16 |
| R06 | Phase 6；AC 5、6 | HTTP(S) 限制、DNS／實際連線 IP／redirect SSRF 防護、禁 proxy 繞過、有限資源、禁遠端程式執行、禁洩密 | [資料與安全](data-flow.md#network-boundary)；AC02–AC03、AC06、AC12–AC13 |
| R07 | Phase 7；AC 8 | 每個 checker 的 unit tests、本地 HTTP(S) integration、SSRF／redirect／timeout／取消／評分測試 | [驗收](acceptance-criteria.md)；AC04–AC17 |
| R08 | Phase 7、Deployment；AC 9 | Windows／Linux 自架；設定、執行、部署、NEAR token／註冊文件與 sample reports；環境變數設定 | [操作面](ui-spec.md#operator-surface)；AC17–AC18 |
| R09 | Objective、Technology、Out of Scope | .NET 10、HttpClient／SslStream、AngleSharp、Hosting、Http、System.Text.Json、xUnit；無 AI 依賴 | [排除範圍](#out-of-scope)；AC17 |

S1 的 phase 標題是 Issue body 的文字定位；各列以 S1 URL 加標題／AC 序號定位，不假造逐節 GitHub HTML anchor。

## Out of scope

AI／LLM／OpenAI／Claude／付費 AI 服務、Playwright、Lighthouse／PageSpeed、WCAG、全站爬取、滲透測試、PDF、dashboard／frontend UI。外部資料庫 MVP 不採用；若發現不可替代的必要性，先提範圍差異，不能自行引入。Docker 僅為可選包裝，不是必要交付。

不主動投標、發布 listing、接觸錢包或發送資金；R05 只處理已分配工作。也不替委託人登入網站、執行 JS、送表單或下載附件。這些是依非侵入式目標提出的範圍解讀，與下列 D01 一併待審。

## 工程提案

P01：單一 owning slice 覆蓋 S1，實作計畫依安全邊界 → 本地 audit → 報告 → NEAR adapter 分段，避免安全防護最後才補上。

P02：使用輪詢 REST 的 Worker，不設對外 HTTP API／webhook。保留單次本地執行供維運與離線驗證；public HTTP server 與前端不在本包。

P03：單一 Worker instance，使用持久化本地檔案追蹤任務與待提交報告；記憶體去重不足以覆蓋重啟。不宣稱跨主機 exactly-once。

P04：未決事項只擋相依路徑。獨立模型、離線 fixture、序列化及規格核對可於 P0 批准相應範圍後先行；未知契約不能由 fake production adapter 代替。

## 待決事項

2026-10-26 使用者在規格交付後指示「開始實作」；本次採用下列 D01/D02/D03/D04/D06/D07 已列的保守建議，D05 維持 pending。Owner 為產品負責人／維運者；沒有將外部市場操作或費用納入此授權。

| ID | 待確認事項與本包建議 | 受影響工作／解除條件 |
| --- | --- | --- |
| D01 | 平台 brief 格式：建議 `job.description` 必須是只含 `url` 的 JSON object；不猜自然語言或多 URL；缺值進 NeedsReview | NEAR input adapter、AC14、AC16；確認格式與錯誤回覆，或提供真實 assignment 範例完成 mapping |
| D02 | 掃描邊界：建議只允許 80/443、同 origin 限量 link probes；主頁 redirect 可跨公開 origin，每跳重驗；拒混合公私 DNS；預算見 data-flow | 網路 transport、URL validator、AC02–AC03、AC06、AC09、AC12；審閱並確認邊界與預算，不放寬未決政策接實網 |
| D03 | severity／cookie 適用性／評分／grade：採 domain 的保守規則與版本化扣分提案；不把無法識別的 cookie 當認證 session | checker findings、scoring、AC04–AC05、AC07–AC11；確認規則表與 partial score 呈現 |
| D04 | 本地 journal／report 保存：建議單 instance、atomic files、完成後保留 7 天；執行中及不明提交不自動刪除 | repository、恢復、部署、AC15、AC18；確認可寫磁碟／ACL、容量與保留期限 |
| D05 | 平台真實驗證：尚未提供 sandbox/test host、token、agent、assignment；官方可自聘 draft agent，但不代表免費或 sandbox | NEAR lifecycle AC16、正式接單；維運者提供官方支援測試路徑與明確測試授權，實際走通且留去識別證據 |
| D06 | request-changes：建議新 SLA window 作為 revision；僅相同格式有效 URL 可自動重跑一次，其他 feedback 停 NeedsReview | revision detection、AC14–AC16；確認 revision 辨識依據與重新提交範圍；不能只以任意 message 判新 revision |
| D07 | 平台 submit 不明結果：官方未承諾 submit idempotency key；建議持久化 pending submission，讀回狀態，仍無法確定時人工處理，不盲重 POST | submit retry／restart、AC15；確認對帳策略並完成 timeout-after-commit 測試，才能宣稱安全冪等 |

## 差異與 writeback

S3 無實作，沒有可比對的已建行為或已通過測試；不能把 S1 checklist 當成完成。NEAR 查核補充了 start call、文字 brief 與提交不明結果的限制，屬契約澄清，未更動 Issue。

儲存庫沒有指定 `writeback_required`、`writeback_status` 的位置或允許值，本包不自行發明欄位。此次只新增 docs，不寫回 GitHub、不改需求權威。未來若工程推導改變 S1 範圍，先於本節記錄 delta 與來源，待使用者同意後才寫回；若引入正式治理規則，再依其位置和詞彙遷移。

## 確認 gate

2026-10-26 使用者「開始實作」已解除本包開工 gate，證據見 [計畫 P0](implementation-plan.md#progress)。D05 真實市場測試仍未解除；開工不代表驗證、驗收、接單或發布完成。
