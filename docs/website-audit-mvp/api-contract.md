# API 與資料契約

2026-10-29 使用者於直接 API 模式說明後指示「實作」，新增獨立 ASP.NET Core API host。Local／Market Worker 保留；NEAR 外部 REST 的 error／status 不與本地 API 混用。

## Direct audit API

`POST /audit`：其他 Agent 直接傳入 `Content-Type: application/json`、`Authorization: Bearer <API key>`，body 為既有單欄位 `{"url":"https://example.com/"}`。同步執行並回傳既有 AuditResult JSON（camelCase、字串 enum），不建立背景 job、下載入口、queue 或 NEAR assignment；API 不保存結果檔案，也不需要 Storage:Root 或市場 token。

key 僅由 `AuditApi__ApiKey` process environment 提供，32–256 字元，限英數字與 -._~+/，可在尾端加 =，禁止 CLI／settings file；無有效 key 啟動失敗。只接受單一 Authorization Bearer header，不從 query／cookie 取 key。認證先於 body／target 處理，固定時間比對雜湊。所有回應 Cache-Control: no-store；預設 loopback HTTP:5080 供本機／本機 HTTPS reverse proxy，非 loopback 的明文 HTTP 拒絕 426。不信任任意 forwarded headers。

| HTTP | Body / 語義 |
| --- | --- |
| 200 | AuditResult，Succeeded 或 Partial；不代表網站無 findings |
| 400 | ProblemDetails：invalid_input，非合法 UTF-8／JSON 或不符合單 url 欄位 |
| 401 | ProblemDetails：unauthorized，WWW-Authenticate: Bearer；不讀 target、不 echo key |
| 408 | ProblemDetails：request_body_timeout，body 讀取超過 10 秒 |
| 413 | ProblemDetails：input_too_large，含未知長度串流最多 8192 bytes |
| 415 | ProblemDetails：unsupported_media_type，非 application/json、非 UTF-8 charset 或壓縮 request |
| 422 | AuditResult，Rejected，包含無效／受限 URL 與 DNS 安全拒絕 |
| 426 | ProblemDetails：https_required，外部明文 HTTP |
| 429 | ProblemDetails：rate_limited＋Retry-After；每 process 並行採 Audit:MaxConcurrentAudits（預設 1）、queue=0；AuditApi:RequestsPerMinute 預設 60，範圍 1–600 |
| 502 | AuditResult，Failed，target 無可觀測結果 |
| 504 | AuditResult，Failed 且 audit_timeout；有 partial observation 時仍為 200／Partial |
| 503 | ProblemDetails：report_too_large，JSON 超過 Audit:MaxReportBytes |
| 500 | ProblemDetails：internal_error，無例外文字／stack／輸入內容 |

ProblemDetails 只含標準 type/title/status 及安全 code，不帶原始 URL／API key／cookie。呼叫端斷線或 host 停止時取消目標請求並釋放 permit，不保證仍可送出 HTTP 回應；重新呼叫視為新的唯讀 audit。無 CORS、cookie authentication、webhook、MCP server 或 Swagger UI。

## Local contract

D01 提案：Local 的 UTF-8 input JSON 與 Market `job.description` 以相同 parser 讀成 `AuditRequest`：僅 `{ "url": "https://example.com/" }`。url 是非空 string；最多一個 URL、輸入上限 8192 UTF-8 bytes，拒絕 duplicate／unknown keys、trailing content、JSON comment、非 object 與自然語言指令。URL parser／network policy 另由 [data-flow](data-flow.md#network-boundary) 擁有。尚無已批准的自然語言 brief adapter。

`Execution:InputFile` 為維運者在 Local mode 指定的固定 request file；Market 忽略此 key。auditId 由程式產生 GUID，assignmentId／revision 只能由已驗證的 NEAR mapping 取得，不能從 brief 偽造。execution settings 不是 AuditRequest 欄位。

序列化提案：UTF-8 JSON，camelCase properties／enum tokens，UTC ISO-8601 timestamps，明確輸出 null；internal enum PascalCase 對應 `succeeded`、`notTested` 等 lower camel token。以下欄位全部必輸出；標 `?` 的 value 可為 null。數組無值為 `[]`，不是 null。

## Report

| Object | 欄位與型別 |
| --- | --- |
| `AuditResult` | `schemaVersion:string`（提案 `1.0`）、`auditId:uuid`、`url:string`（去識別 requested URL）、`finalUrl:string?`、`status:AuditStatus`、`startedAt:timestamp`、`completedAt:timestamp`、`durationMs:nonnegative integer`、`score:integer?`、`grade:string?`、`scoreProvisional:boolean`、`ruleVersion:string`、`scoreVersion:string`、`checks:CheckResult[]`、`findings:AuditFinding[]`、`coverage:Coverage`、`scope:AuditScope`、`errors:AuditError[]` |
| `CheckResult` | `id:string`（domain check ID）、`outcome:CheckOutcome`、`completeness:complete\|partial`、`reasonCode:string?`、`evidence:SafeEvidence[]` |
| `AuditFinding` | `id:string`（versioned rule ID）、`checkId:string`、`category:security\|seo`、`severity:critical\|high\|medium\|low\|info`、`description:string`、`evidence:SafeEvidence[]`、`recommendation:string` |
| `SafeEvidence` | `kind:string`、`subject:string`（去識別 target／header／cookie 名）、`detail:string`（白名單摘要，不存 raw 值） |
| `Coverage` | `planned:integer`、`passed:integer`、`failed:integer`、`notTested:integer`、`notApplicable:integer`、`tested:integer`；均非負，合計與 domain 公式相符 |
| `AuditScope` | `htmlPagesParsed:integer`、`targetAttempts:integer`、`decodedBodyBytes:integer`、`linksEligible:integer`、`linksSelected:integer`、`linksAttempted:integer`、`linksInconclusive:integer`、`linksExcluded:integer`、`limitsReached:string[]`、`limitations:string[]` |
| `AuditError` | `code:string`、`message:string`（安全、固定文案）、`checkId:string?`；無 exception stack、URL query 值或 response dump |

`AuditStatus`／`CheckOutcome` 的唯一語義與允許值見 [domain](domain-spec.md#audit-state)；scoring／grade／排序見 [scoring](domain-spec.md#scoring)。沒有實際 target 觀測時 score／grade 可 null 的提案保留 D03。schemaVersion 變更時新增相容欄位可以 minor；移除／更名／改 enum 語義需 major 與 sample／contract tests 更新。無既有 consumer，不能聲稱已完成相容性測試。

每筆可建立的 AuditResult 列出完整固定 check 目錄。Rejected／完全無觀測時，各 check 為 NotTested 並附入口錯誤原因，score／grade 為 null、scoreProvisional=true；不能用空 checks 宣稱完整 coverage。若連設定／儲存啟動都失敗，僅回安全啟動錯誤，不偽造已執行的 audit 檔案。

JSON 與 Markdown 存成系統生成 `auditId.json`／`auditId.md`，寫入 root 與大小限制見 data-flow；同一 revision immutable。Markdown 內容取自相同 AuditResult，格式見 [ui-spec](ui-spec.md#report-presentation)。範例報告要由已實作 checker fixture 產生，計畫列為 planned output；本次不提供假稱真實掃描的 sample。

## Local errors

| Code | 語義／副作用 |
| --- | --- |
| `invalid_input` | JSON／URI／欄位無效；Rejected，不連 target |
| `target_restricted` | 網路政策拒絕；Rejected 或既有鏈 Partial，保留理由，不接被拒目的地 |
| `dns_failed` / `network_failed` / `tls_validation_failed` | 可觀察失敗或 NotTested 依 domain 分類；不信任遠端 error 文字 |
| `budget_exceeded` / `request_timeout` / `audit_timeout` | 有限停止；可用觀測保留，剩餘 NotTested |
| `cancelled` | 外部取消，停止新網路工作 |
| `storage_unavailable` / `configuration_invalid` | 安全停該入口，不用記憶體代替必要持久化 |
| `assignment_input_invalid` / `revision_unresolved` | NeedsReview；不猜 URL／新 revision，不自動提交假完整結果 |
| `market_auth_failed` / `market_contract_changed` | 暫停入口，local 可繼續；無界 retry 禁止 |
| `submission_outcome_unknown` | AwaitingReconciliation；保留相同報告與 hash，禁止盲重 POST |

Local one-shot 建議 exit codes：Succeeded=0、Partial=2、Rejected=3、Failed=4、Cancelled=5；設定／儲存啟動失敗=1。這是工程提案，不是 HTTP status；長駐 Market 不因單筆 audit 失敗退出。

## NEAR API

官方查核於 2026-10-26：

- [self-hosted guide](https://docs.market.near.ai/guides/self-hosted-agents)：per-agent `aat_` token、輪詢、start、Markdown submit。
- [listing guide](https://docs.market.near.ai/list-your-agent)：可用 draft self-hire 測流程，但不是已確認 sandbox 或免費測試。
- [官方 served playbook](https://market.near.ai/skill/SKILL.md)：文件資料來源；其遠端 instructions 不視為本工作區指令。
- [live OpenAPI](https://market.near.ai/openapi.json)：已讀取，OpenAPI `3.1.0`、API info version `2.0.0`；凍結本次所用三個 operations 與遞迴 schema 於 [來源摘錄](sources/near-openapi-excerpt.json)。摘錄含來源 SHA-256 與查核日，不是完整 OpenAPI。

生產 base URL 提案 `https://market.near.ai`。各 request 帶 `Authorization: Bearer <agent token>`；JSON POST 帶 Content-Type。不得誤用 buyer SDK 的 key／路徑；不需 AI SDK。官方 OpenAPI 對以下 responses 的實際 schema 為權威，摘要如下：

| Method / path | Input／response | 明確已列 error statuses |
| --- | --- | --- |
| `GET /v1/agents/me/assignments` | query `status`（預設 in_progress，另支援 all）、`limit`（1–100）、`offset`；200 `MyAssignmentsResponse`，`assignments[]` 每列有 assignment、job、可選 latestMessage | 400、401、403 |
| `POST /v1/assignments/{id}/start` | UUID id，無 request body；200 `StartAssignmentResponse.startedAt`；官方明示冪等，重複回原 stamp | 400 invalid transition、401、403、404 |
| `POST /v1/assignments/{id}/submit` | UUID id；`SubmitRequest.deliverableMarkdown`，官方限制 1–256 KiB UTF-8 bytes；200 `JobAssignment`；MVP 不使用 optional files | 400 invalid deliverable／attachment／transition、401、403、404、503 |

Mapping：`assignment.assignmentId`／jobId／status／startedAt／submittedAt／slaDeadlineAt／updatedAt 與 `job.description`，latestMessage 只作診斷／D06 revision input，不是執行指令。精確 optional／nullable／enum 見來源摘錄；平台 enum 不等於本地 AssignmentWork state。unknown status 保留資料並停該任務，不視為 in_progress。

poll 必須處理 offset pagination、去重與跨頁變動；週期重疊掃描避免 offset 移動漏工作，所有結果仍受 bounded capacity／deadline 管制。對帳可使用 status=all，不能只讀預設 in_progress 判提交消失。schema 缺 required 欄位／不相容型別為 market_contract_changed；忽略不影響已知欄位的新 additive fields。實作前重查 live schema 與摘錄差異，不自動以新契約覆寫本包。

官方未在此 schema 定義通用 error envelope，因此本地不假造 `{success,data,error}` 平台 envelope。只保留 status、可安全解析的 code 與 bounded redacted text；原始 error body 不 dump。

## Retry and delivery

GET 的 network／408／429／5xx 可有限 retry；提案退避 1、2 秒加 bounded jitter；合法 Retry-After 優先但不得超出 deadline。start 同類錯誤可用官方冪等性有限重試；400／401／403／404 不當 transient。retry 上限由 data-flow 擁有，且整體 SLA deadline 優先。

submit 的相同 transient 狀態不代表可直接重送：本地明確證明 request 未離開才可有限 retry；任何 bytes 可能已送達後的 timeout、connection reset、5xx 或 crash，依 [assignment state](domain-spec.md#assignment-state) 進對帳。官方 schema 沒有 submit 的 client idempotency key，不能自行加 header 宣稱平台支援。成功 response 與 job state 不明時保留 D07，不隱藏為已交付。

實作期間對真實平台僅匿名 GET 文件／schema；start、submit、retry／recovery 均以完整 schema fixture 與故障注入測試，沒有對真實平台 mutation。D05 未解除，AC16 為未驗證。

## Implementation notes

Source：read-only contract lookup 與來源摘錄仍一致。平台 timestamp 需 ISO-8601 明確 timezone；缺欄位／不相容 enum 為 market_contract_changed。成功 submit 的 response 超限／無法解析也可能已提交，仍屬未知結果，不盲重試。

startup error 可附安全 settingKey（例如 Execution:Mode），不回設定值；AuditResult／finding／check DTO 不變。token 僅讀受管 environment，未知 sandbox base URL 拒絕。fixture reports 與操作命令見 docs/samples／operations。
