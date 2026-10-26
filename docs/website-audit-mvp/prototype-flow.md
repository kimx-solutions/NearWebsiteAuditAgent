# Website Audit MVP 流程

本文件為操作與恢復流程契約，來源與未決事項見 [requirement](requirement.md#待決事項)。沒有 UI prototype；狀態名稱定義於 [domain](domain-spec.md#audit-state)，欄位定義於 [API](api-contract.md)。

## Local success

1. 維運者以 local mode 提供一筆 AuditRequest，載入設定並做啟動驗證；不載入或要求 NEAR token。
2. 驗證 URL 語法與網路政策，再建立有限預算與取消 deadline。
3. 安全 transport 取得主頁，手動處理 redirect；建立 HTTP／TLS 觀測 snapshot。
4. orchestrator 依共用 snapshot 執行 security／cookie／SEO checks，再取得 robots、sitemap 與選定 links 的有限觀測。
5. 每個 check 產生明確結果；不可執行或依賴不足者是 NotTested，已確認不適用者是 NotApplicable。
6. 固定排序後計分，以相同結果產生 JSON、Markdown；完成 atomic 寫入。操作結果為 Succeeded 或 Partial，不能把 partial 當全數通過。

## Market success

1. 載入 market mode 的 token／base URL／本地 journal；確認單 instance lock 與目錄可寫。未完成啟動安全條件時不取得工作。
2. 輪詢平台已分配任務，處理 pagination；去重並檢查狀態、revision、SLA 與本地剩餘 capacity。只做 ownership 已受 token scope 限制的工作。
3. 解析 D01 輸入，URL／設定驗證成功後持久化任務；不因 brief 指令變更掃描政策。
4. 取得本地任務 lock，宣告 start，確認成功再產生對目標的網路流量。start 成功不能延長平台 SLA。
5. 執行 Local success 的步驟 2–6，deadline 取平台 SLA 扣除提交餘裕與本地最大時限之較早者。
6. 持久化報告、hash 與 Submitting 意圖，呼叫官方 submit；明確成功後記 Submitted。完成 audit 和成功提交是兩個不同事件。
7. 後續 poll 已提交／終止狀態時不重掃、不重送。平台接受與付款不屬 audit 成功判定。

## Alternate and recovery

| 事件 | 可觀察行為與恢復 | 對應驗收 |
| --- | --- | --- |
| 非 HTTP(S)、userinfo、受限 port/IP、無效 URL | Rejected；無目標 HTTP/TLS 連線；本地回傳安全錯誤；market 任務停 NeedsReview，不偽造報告 | AC02、AC14 |
| 主頁 DNS／連線失敗或憑證驗證失敗 | 保留已取得的診斷；主頁依賴 checks 為 NotTested；有觀測則 Partial；完全無觀測則 Failed。不可略過驗證繼續抓 HTML | AC03–AC04、AC11 |
| HTML 不完整或非 HTML | 可用 HTTP／TLS checks 繼續；HTML checks 為 NotTested，說明原因 | AC08、AC11 |
| redirect loop、越界或達上限 | 立即停止該鏈；產生 loop／limit／policy reason；主頁內容依賴 checks 不當通過 | AC06 |
| link／robots／sitemap timeout、429、5xx | 記錄不可測與範圍；其他 checks 繼續；404/410 依各 checker 明確判定，不混為網路錯誤 | AC08–AC09 |
| 預算或 SLA 不足 | 停止新請求；保留可用結果及 NotTested 原因；在還有提交餘裕時可提交 Partial，不能聲稱完整 | AC12–AC13、AC16 |
| user／host 取消 | 停止新工作並傳遞 cancellation；已完成資料可保留。market 不在取消的 token 下啟動新的提交；恢復後依平台狀態對帳 | AC13、AC15 |
| 平台 401/403 | 不重試憑證錯誤；暫停市場入口並記錄去識別錯誤；local mode 不受影響 | AC14 |
| 平台暫時失敗 | 按 API 的有限 retry／deadline 策略；單一任務失敗不終止 Worker | AC14–AC15 |
| POST submit 已送出但未收到明確結果 | AwaitingReconciliation；保留原始報告、revision、hash；讀回任務，禁止盲目重送 | AC15 |
| 進程在 start 後或 submit 中重啟 | 從 journal 恢復並讀回平台；不能用 `startedAt != null` 跳過自己的未完成任務；未知狀態停待對帳 | AC15 |
| journal 無法寫入、損毀或 instance lock 已存在 | 不處理新市場任務；保留檔案，提示維運者；不可退回純記憶體去重 | AC15、AC18 |
| request-changes／新的 SLA window | 依 D06 重新驗證輸入、建立新 revision；未能明確辨識或超過重跑限制則 NeedsReview；不把一般訊息當新單 | AC16 |

## Review boundary

NeedsReview 不自動發送買方訊息、投標、接受付款或更改平台 listing。未來如增加自動澄清訊息，需另定文案、冪等與授權範圍。本輪沒有呼叫任何平台 mutation。
