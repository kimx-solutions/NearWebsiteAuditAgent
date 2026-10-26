# Domain specification

所有具體狀態、check 規則與 scoring 在此擁有；來源為 R01–R07。D03／D04／D06／D07 的保守提案已隨開工採用；實作澄清見文末。序列化名稱與欄位唯一來源為 [API](api-contract.md#local-contract)。

## Model

- `AuditRequest`：單一目標與系統產生的 audit identity；不能攜帶可放寬安全設定的選項。
- `AuditObservation`：一次有限執行取得的 HTTP／TLS／HTML／probe snapshot、policy version、預算使用與觀測 UTC 時間。checker 為純函式，不能私自連網。
- `CheckResult`：固定 check ID、result、reason code 與安全 evidence；findings 只代表已觀察問題。
- `AuditFinding`：穩定 rule ID、check ID、category、severity、description、evidence、recommendation；同 check 可有多個 findings，計分只扣一次最高權重。
- `AuditResult`：一筆 audit 的狀態、checks、findings、coverage、score／grade、限制與版本；由 orchestrator 聚合。序列化不含原始 HTML／cookie values。
- `AssignmentWork`：平台 identity／revision、本地狀態、SLA deadline、audit ID、durable report reference、submission intent 與對帳資料。與 AuditStatus 是兩個 lifecycle。

## Audit state

`AuditStatus`：`Pending → Rejected | Cancelled | Running`；`Running → Succeeded | Partial | Failed | Cancelled`。

- Rejected：輸入或政策驗證未通過，可能已做 DNS 判讀，但沒有對被拒的 target 建立 HTTP/TLS 連線。
- Succeeded：所有適用 checks 均有可判定結果，沒有 NotTested 或 partial completeness；不代表網站無 findings。
- Partial：至少一個 check 有可判定結果，另有 NotTested／預算不足；已取得的失敗 evidence 不丟棄。
- Failed：無可判定的 target check，或無法產生／保存必要輸出；安全錯誤仍回傳。
- Cancelled：明確 cancellation；如已產生觀測可保留，但不能偽裝為 Succeeded。

`CheckOutcome`：`Passed | Failed | NotTested | NotApplicable`。NotApplicable 需確定不適用及 reason；例如 HTTP-only 觀測上的 HSTS、不存在的 cookie。抓取失敗不能推論 cookie 不存在，需 NotTested。`NotTested` 包含 timeout、依賴不足、政策阻擋、超限、unsupported grammar；不算 Passed。

## Checks

D03 已隨開工採用，每列 severity 固定為 `rules-v1`。header 名與 token 按各規範大小寫處理，HTML whitespace trim。列表為 MVP 明確範圍，不宣稱完整 browser policy 或安全合規驗證。

| Check ID | 判定與 evidence／建議 |
| --- | --- |
| `tls.https` | 對目標 HTTPS 或已驗證同 host:443 probe，握手與有效驗證成功為 Passed；明確拒絕連線／未提供 HTTPS 為 Failed（High），timeout／DNS 不明為 NotTested；建議提供可信 HTTPS |
| `tls.certificate` | 記有效期、chain trust、hostname policy error；過期、尚未有效、hostname mismatch／untrusted 為 Failed（High）；error 可觀察但握手被拒仍保留診斷；無憑證資料則 NotTested；建議修正憑證／chain |
| `tls.protocol` | 僅判讀正常 handshake 的協商協定：TLS1.2／1.3 Passed；能觀察到舊協定則 Failed（High）；握手失敗無版本則 NotTested；不強制開啟舊協定來探測 |
| `headers.hsts` | HTTPS response 缺／空 header、無法解析 max-age 或 max-age=0 為 Failed（Medium）；正整數 max-age Passed；不因未設 includeSubDomains／preload 扣分；HTTP response 不適用 |
| `headers.csp` | 缺／空 enforced CSP 為 Failed（Medium）；僅 report-only 不算 enforcement。MVP 解析 directive/token，重複或無法判讀的語法 NotTested；有效的 `script-src *`／`unsafe-eval` 為 Failed（Low）；其餘可判讀政策 Passed，僅代表本規則未發現列舉問題，不代表防護完整；nonce／hash 等組合不能用 substring 判漏洞 |
| `headers.framing` | 有有效 enforced CSP frame-ancestors 時優先判 CSP：none/self 或明確 host 來源 Passed；`*`／僅 scheme 廣泛允許 Failed（Medium），不可用 XFO DENY 覆蓋該結果。無 frame-ancestors 時 X-Frame-Options DENY/SAMEORIGIN Passed，兩者均無／僅 ALLOW-FROM／無效值 Failed（Medium）；有效政策無法確定時 NotTested；建議配置限制 framing 的政策 |
| `headers.nosniff` | X-Content-Type-Options 的 token 為 nosniff Passed，缺／其他值 Failed（Low） |
| `headers.referrer` | 最後一個可辨識 Referrer-Policy token 決定：unsafe-url 或 no-referrer-when-downgrade 為 Failed（Low）；no-referrer、same-origin、origin、strict-origin、origin-when-cross-origin、strict-origin-when-cross-origin Passed；缺／無可辨識 token Failed（Low） |
| `headers.permissions` | 缺／空 Permissions-Policy Failed（Low）；可判讀 feature allowlist 語法 Passed；語法不支援／有歧義 NotTested；不要求所有 feature 禁用，不聲稱未測 feature 安全 |
| `cookies.secure` | 每個 HTTPS Set-Cookie 缺 Secure 為 Failed（Medium）；HTTP 發 cookie 缺 Secure 亦記 Medium，但不能認為加 Secure 即解決 HTTP 傳輸；建議 HTTPS 與 Secure。完整可讀 response 沒 cookie 則 NotApplicable |
| `cookies.httponly` | 有 HttpOnly 為 Passed；缺少但認證適用性未知為 NotTested，附 Info「未設，是否需要取決於用途」，不宣稱已識別認證 session；有經維運者核准認證 cookie policy 才可用 Failed（Medium），MVP 不自行從 cookie 名猜用途 |
| `cookies.samesite` | SameSite=None 但無 Secure Failed（High）；缺或無效 SameSite Failed（Low）；Lax/Strict 或 None+Secure Passed。Expires/Max-Age 僅區分 persistent/nonpersistent，不等同 authentication/session。所有 evidence 僅 cookie name／flags／分類，無值 |
| `redirects.upgrade` | HTTP 鏈終點為可信 HTTPS Passed；明確仍 HTTP 或 HTTPS 降級 Failed（Medium）；HTTP probe 不可達 NotTested，不把 HTTPS 已可用當 HTTP 升級成功 |
| `redirects.chain` | 有限鏈到非 redirect response Passed；loop／超過 hop limit Failed（Medium）；不安全 destination 停止並 Failed（High）；無 Location／網路不明 NotTested；只記去識別 hop URL 與 status |
| `seo.title` | 主頁 HTML 有且只有一個非空 title Passed；缺、空或多個 Failed（Low）；不引入未確認的字數規則 |
| `seo.description` | 一個非空 meta name=description Passed；缺、空或多個 Failed（Low）；不做 AI 文案評估 |
| `seo.h1` | 恰一個非空 H1 Passed；零個／空／多個 Failed（Low）；只計主頁 DOM，不計全站 |
| `seo.canonical` | 一個可解析到 HTTP(S) 的 canonical Passed；缺、無效、多個 Failed（Low）；只觀察不跟隨。合法相對 URL 依主頁解決；跨域本身不判錯 |
| `seo.robots_meta` | 觀察 robots meta tokens，合法 directives 可讀為 Passed；noindex/nofollow 只 Info，不能在無業務意圖下視為錯誤；無 directives 為 Passed 並註明未明示；相互矛盾 token Failed（Low），無法判讀 NotTested |
| `seo.robots_txt` | `/robots.txt` 2xx 且有界可讀文字 Passed（只驗存在／可讀）；404/410 Failed（Low）；其他非成功／超限／timeout NotTested；不推論允許索引或遞迴解析規則 |
| `seo.sitemap` | `/sitemap.xml` 2xx、有界且可解析成 sitemap urlset/sitemapindex XML Passed；404/410 或 2xx 明確非 sitemap Failed（Low）；其他非成功／超限／timeout NotTested；禁 DTD／外部 entity，永不抓子 sitemap |
| `seo.links` | 按 data-flow 有限抽樣，404/410 為 Failed（Low）並列去識別 link；401/403 僅 Info（受限不等於壞鏈）；429/5xx／transport／越界為 NotTested。若含明確壞鏈與不明項，check 為 Failed 且 completeness=partial；零 eligible link 才 NotApplicable |

所有列都有 AC04–AC09 的 fixture 驗證路徑；同一 checker 產生 findings 時必須附安全 evidence 與具體改正建議。觀測缺失不生成「missing header」等假 finding。一個 check 的明確 failure 可以與同 check 的 partial completeness 共存，不能用 failed outcome 隱藏未測樣本。

## Scoring

D03 採用 `score-v1`：severity weights 為 Critical=25、High=15、Medium=8、Low=3、Info=0。嚴重度值包含 Critical 以符合 R04，但本版不因單純少 header 自動產生 Critical。

1. 固定依 check ID 分組，只取各 check findings 的最大 weight，再求和；同名 cookie／多個壞鏈不反覆扣同一 check。
2. 至少一個適用且可判定的 check 才產生 `score = max(0, 100 - sum(weights))`，範圍 0–100；無觀測為 null 並有安全錯誤，不偽造 100。null 是 D03 已採用的不可測例外，不把零觀測當 100。
3. coverage 記 planned、passed、failed、notTested、notApplicable counts；`tested=passed+failed`，適用分母為 planned-notApplicable。另列 link selected／attempted／inconclusive／excluded 數量與 completeness。
4. 任一適用 check NotTested 或 completeness=partial，`scoreProvisional=true`、grade=null，明示樣本有限；未觀察部分不扣猜測分、不寫 Passed。無不明項時 scoreProvisional=false，A≥90、B≥80、C≥70、D≥60、F<60。
5. JSON／Markdown 使用同一結果；按 severity weight 降序、check ID、rule ID、安全 evidence key ordinal 排序。相同完整 observation snapshot、clock 與版本必得相同 findings／score／grade／ordering；duration、audit ID 等執行 metadata 不要求跨次相等。

NotApplicable 與 NotTested 原因及 rule／score version 一起輸出，防止把「未測到」解讀為「安全」。規則更新需新版本與受影響 AC／sample 差異，不默改歷史分數。

## Assignment state

本地狀態提案：`Discovered → Validated → Starting → Running → ReportReady → Submitting → Submitted`。

- 輸入／revision 不可解析：NeedsReview；恢復須有明確決策與重新驗證。
- 任何目標執行失敗：AuditStatus 依上節；有可交付 Partial／失敗摘要時可進 ReportReady，不能把無結果假報告當完整成功。
- submit 已開始但結果不明：AwaitingReconciliation；此狀態先 durable，再處理 retry／restart。
- 平台終止／不再屬於有效 revision：Stopped；本地 Submitted 不等於平台 accepted／已付款。
- host shutdown 的 Running 留存 checkpoint；不能保留開啟 socket，重啟要重新驗證 DNS／平台 deadline，不能沿用舊 IP 安全判斷。

invariants：每個 assignment+revision 同時只有一個本地執行；平台已 start 的自己的未完成工作可恢復。平台 SLA deadline 不因本地 retry 延長。submitted 結果不再自動重送；request-changes 只有確認新 revision 才解鎖。D06 建議以 assignment ID + 新的 slaDeadlineAt 辨識 revision，需用實際平台樣本驗證，普通 message／updatedAt 不能單獨作依據。

start 官方為冪等；submit 無已確認冪等鍵。提交前記錄 report hash／revision／intent，timeout 或 crash 後先 poll 平台目前狀態；可確定已 submitted／terminal 就不重送，可證明未送出才允許有界 retry。仍可能在途且平台仍 in_progress 時保持 AwaitingReconciliation，需維運者對帳，不宣稱 exactly-once。以 D07 作 production gate。

## Repositories

Core ports 提案：`ISafeTargetTransport`、`IAuditChecker`、`IClock`、`IReportWriter`、`IAssignmentRepository`、`IMarketClient`。前兩者分離網路與純判定；clock 可注入讓憑證有效期／SLA 測試可固定；transport test seam 不能成 production SSRF off switch。

`IAssignmentRepository` 最少保存 agent/assignment ID、revision、state、SLA deadline、audit ID、attempt counts、受保護 target URL、report relative paths／hash、last known platform status、last safe error／updated UTC。claim/update 在單 instance 下原子化；crash 不可留下半份可讀 final record。報告為 immutable per revision；修改另起 revision，不能讓 pending submission 對到不同內容。

檔案 lock、atomic write、容量、ACL 與 retention 規則由 [data-flow](data-flow.md#persistence-and-secrets) 擁有。無 schema migration 資料庫；journal schema version 若改版需相容讀取或安全拒絕並保留原檔，不自動清空待交付工作。

## Test boundaries

checker unit tests 用明確 observation fixture；orchestrator 測取消／預算／聚合；transport integration 使用可控 HTTP/TLS server、resolver、socket 及憑證時鐘。只有 Tests 專案可注入本地 fixture transport；production classifier 必須繼續拒絕 loopback。NEAR adapter 以官方 schema fixtures 加故障注入驗證 retry／未知結果，再單獨執行有授權的真實 test assignment；mock 通過不能取代 AC16。

## Implementation clarifications — 2026-10-26

- MarketWorkProcessor 位於 Core，透過 IReportContentReader／IMarketClient／IAssignmentRepository 等 ports 處理 use case；Infrastructure 只提供檔案／REST adapters。CheckEngine 及 pure check groups 取代額外 IAuditChecker interface；時鐘使用內建 TimeProvider，不另造 IClock。
- Discovered 是 poll 的 transient input；持久化從 Validated 或 NeedsReview 開始，未對外宣稱可查詢的 Discovered record。
- Journal 另記 ResultStatus／ObservedChecks，確保未知或零觀測結果不能啟用付費 gate。驗證 metadata 與正式接單開關詳 data-flow；沒有新增買方訊息或 listing 行為。
- 相同 check／rule／severity findings 合併，保留前三份安全 evidence 與省略數；扣分仍取每 check 最大 severity，語義不變。
- 空白 canonical 不算有效；未知 CSP directive／無法判讀的 framing source 保留 NotTested。這是有限規則集，非完整瀏覽器 CSP evaluator。

Cookie 觀察補充：CheckResult evidence 與 Markdown 均包含 safe name／Secure／HttpOnly／SameSite／lifetime=persistent、nonpersistent 或 unknown，purpose 一律 unknown；不儲存 value 或 expiry 原文。每 check 最多三筆明細與一筆省略摘要。有效 Max-Age 優先，其次有效 Expires；期限 attribute 不明時不可當作無期限 cookie。
