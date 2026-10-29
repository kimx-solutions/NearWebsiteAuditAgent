# UI 適用性與操作面

## Applicability

`not-applicable`：S1 Out of Scope 明確排除 dashboard／frontend UI；本 slice 為無畫面的 Worker／直接 API。無畫面、欄位表單、瀏覽器 navigation、權限按鈕、responsive breakpoint、視覺 prototype 或 Markdown wireframe 需要實作。`senior-ux-reviewer` 的 `design-time` 與 `implemented-ui`、`frontend-feature-development` 均不適用，理由同步於 [計畫](implementation-plan.md#not-applicable)。

這不免除輸入驗證、狀態可觀察性、報告可讀性與 server-side ownership。若新增 CLI 互動選單、webhook 管理頁或 dashboard，須先修訂 scope／UI／AC，不可沿用本判定跳過 UX。

## Operator surface

2026-10-29 新增直接 HTTP API 操作面，無視覺 UI，兩種 UX 與 frontend 仍不適用。獨立 API process 不使用 Execution:Mode；以 API key 認證 POST /audit 並同步取得 JSON，見 [契約](api-contract.md#direct-audit-api)。

以下為 P02／D04 已落地的操作面，實際命令見 operations：

- `Execution:Mode` 必填為 `Local` 或 `Market`；不得缺省即開始市場輪詢。
- Local 讀一筆 UTF-8 JSON request file，回傳 JSON／Markdown 路徑與穩定 status／error code；不需要 NEAR token。
- Market 僅處理已授權 agent 的 assignments；平台 token 僅由機密設定提供，不作 CLI 引數以免被 process list 捕獲。
- 非互動啟動驗證失敗須非零退出並說明設定 key，不能輸出值。單筆 audit 失敗不得使長駐 Worker crash。
- log 僅含 correlation ID、assignment ID、check ID、狀態、耗時與去識別錯誤；不能 dump request、response、brief、完整 URL 或機密設定。
- Windows／Linux 的服務設定、環境變數、目錄權限、停止與復原流程在實作後的操作文件提供；此處不聲稱 service unit 已存在。

設定欄位／預算在 [data-flow](data-flow.md#resource-budgets)，資料欄位在 [API](api-contract.md#local-contract)，不另複製。

## Report presentation

Markdown 與 JSON 來自同一 AuditResult。Markdown 順序：目標去識別 URL、audit status、分數與 provisional 說明、coverage／scope／limits、各 check 結果、依 severity 排序的 findings／evidence／recommendation、NotTested 與不適用理由、限制與版本。

不可只有「85/B」卻隱藏未測試項目；具體計分語義見 [domain](domain-spec.md#scoring)。惡意標題、header、cookie 名與 URL 必須跳脫，不能生成可執行 HTML、隱藏追蹤圖片或偽裝本系統文案。報告不包含 cookie values、authorization、平台 token、完整 brief 或回應 body。

文案使用可辨識的錯誤 code 與安全說明，例如 `target_restricted`、`budget_exceeded`、`submission_outcome_unknown`。報告不是滲透測試或合規保證，也不能將檢查建議包裝成已確認可利用漏洞。
