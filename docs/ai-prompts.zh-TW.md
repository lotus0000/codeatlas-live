# 給 AI 的 CodeAtlas prompts

[English version](ai-prompts.en.md)

複製下面 prompt，替換 `<...>`。先確認 AI 有本地專案存取權限。
CodeAtlas 沒有在這份文件承諾 MCP 或 AI 自動讀 trace 功能；沒有實際工具時，
請提供去識別化的 viewer 證據或 collector 輸出，不要讓 AI 假裝已看過。

## Prompt 1：接入現有專案

```text
請將 CodeAtlas Live 接入 <專案絕對路徑>，僅用於本地 Debug/Development。
CodeAtlas 原始碼位置：<codeatlas-live 絕對路徑>。
先讀兩個 repo 的適用 AGENTS.md、專案 PLAN/architecture，以及 CodeAtlas 的
README.md、docs/integration.md、docs/limitations.md、docs/usage.zh-TW.md。
先檢查 git 狀態，保留 unrelated changes，遵守專案的 branch 與 task 規則。
若專案已經接入，驗證並補缺漏，不要重複增加 exporter、服務或依賴。
只改必要的啟動、build 和 dev-server 設定，不改業務模組或 generated files。
後端引用和呼叫必須在 Debug/compilation guard 下，production 不含 CodeAtlas。
前端只在 development 初始化；代理只允許 base-aware trace ingestion endpoint，
不可代理 session token、open-file 等管理 API。collector 綁 loopback。
保留既有權限、驗證、OpenTelemetry sampling、exporter 和 enrichment 行為。
確認 source-root 指向被觀察專案；若使用 code-server，處理 absproxy/base/HMR。
CodeAtlas repo 與 Release 套件已公開；不得未經授權公開被觀察的專案、執行中的 collector 或 trace，不要要求我貼 token。
完成後跑相關測試和 Release 隔離檢查，分開報告自動檢查與人工 UI 驗收。
未經明確授權不 push、merge、deploy，也不修改正式服務。
```

## Prompt 2：用執行證據找 bug

```text
請調查 <問題描述>，專案位於 <專案路徑>。
重現步驟：<操作步驟>；預期：<預期行為>；實際：<实际結果>。
CodeAtlas 證據：<去識別化 span/trace 資料、錯誤訊息或 viewer 截圖>。
先讀適用 repo 指示及 CodeAtlas 的 integration/limitations 文件。
先區分 recorded execution、routing metadata、heuristic correlation、exception stack。
只把 recorded spans 當成執行證據；沒錄到的方法或呼叫鏈寫 unknown，不能補想像。
把每個結論對應到實際 trace/span 或 code path；列出尚待驗證的假設。
不要將 source metadata 當作該 method body 已執行，不要將時間接近當作必然因果。
若無法直接讀取 collector，明確說明缺少何種證據，不要聲稱看過 trace。
先重現及確認根因，再做最小修復，補適當測試，保留安全與 Release 隔離。
不要外傳原始 trace、exception、headers、SQL、token 或敏感業務內容。
最後用繁體中文報告：根因、證據、修復、測試，以及仍需人工確認的項目。
```

## Prompt 3：驗收與 Release 發佈

```text
請驗收 CodeAtlas Live <版本> 並發佈到公開 repo <owner>/codeatlas-live 的 Release。
先檢查 repo visibility、remote、git status、套件版本及敏感資料；確認目標是預期的公開 repo，發現 secret 或私有應用資料先停下。
讀 scripts/test-all.sh 和 docs/manual-checks.md，跑可執行的檢查並如實報告人工項目。
用 scripts/pack-local.sh 產生套件，只上傳 NuGet nupkg 與 npm tgz 至 Release。
不得推公開 NuGet/npm，不得改 repository visibility；不用 credentials 寫進文件。
已存在的 tag/asset 不可默默覆蓋；只有驗證完的相同 commit 才能對應 release tag。
檢查發佈後的 public 狀態、tag commit、asset 名稱，下載並比較 SHA-256。
更新使用文件和 AI prompt，區分 registry 發佈與 GitHub Release 檔案分發。
```
