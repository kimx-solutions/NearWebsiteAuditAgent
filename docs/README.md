# NearWebsiteAuditAgent 文件

- [Website Audit MVP 規格包](website-audit-mvp/requirement.md)
- [實作計畫與確認 gate](website-audit-mvp/implementation-plan.md)

`docs/` 是使用者於 2026-10-26 指定的文件 workspace。此包由 GitHub Issue #1 正規化，使用者後續以「開始實作」批准開工。實際進度見 owning plan；真實 NEAR／部署驗收仍與實作完成分開。

## 文件分工

| 文件 | 唯一主要責任 |
| --- | --- |
| [requirement](website-audit-mvp/requirement.md) | 來源、範圍、需求 ID、提案與待決事項 |
| [prototype-flow](website-audit-mvp/prototype-flow.md) | 成功、替代、失敗及恢復流程 |
| [ui-spec](website-audit-mvp/ui-spec.md) | UI 不適用判定、操作面與報告呈現 |
| [data-flow](website-audit-mvp/data-flow.md) | 資料邊界、SSRF、資源預算與機密處理 |
| [domain-spec](website-audit-mvp/domain-spec.md) | 狀態、檢查規則、評分、儲存與不變量 |
| [api-contract](website-audit-mvp/api-contract.md) | 本地 DTO、報告及 NEAR REST 契約 |
| [acceptance-criteria](website-audit-mvp/acceptance-criteria.md) | 可觀察驗收與驗證方法 |
| [implementation-plan](website-audit-mvp/implementation-plan.md) | 唯一實作進度、技能路由、安全 gate 與證據規則 |

另見 [操作文件](operations.md)、[生成的 fixture reports](samples/README.md) 與 [驗證證據](website-audit-mvp/evidence/implementation-verification.md)。本次已建立程式及測試，未部署或操作真實市場。
