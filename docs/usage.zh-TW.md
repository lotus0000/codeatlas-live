# CodeAtlas Live 使用教學

[English version](usage.en.md)

## 公開發佈與套件位置

原始碼放在 `<owner>/codeatlas-live` public repository；NuGet 套件和 npm tarball
放在同一個 repo 的公開 Release。這不是 NuGet/npm registry 發佈；任何人都可以下載。
原始碼與套件公開，但執行中的 collector 和應用程式 trace 仍需保護。不要把 GitHub token 寫進專案、prompt 或文件。

下載頁：[v0.1.0 Release Assets](https://github.com/<owner>/codeatlas-live/releases/tag/v0.1.0)
（瀏覽器下載不需 repo 存取權限）。NuGet 檔案是 `CodeAtlas.AspNetCore.0.1.0.nupkg`、
`CodeAtlas.Otlp.0.1.0.nupkg`、`CodeAtlas.Collector.0.1.0.nupkg`；npm 檔案是
`codeatlas-web-0.1.0.tgz`。它們不在 nuget.org 或 npmjs.com，也尚未設定 private registry。

若要用 CLI 下載，先安裝 GitHub CLI；若 CLI 要求登入，執行 `gh auth login`。

```bash
gh repo clone <owner>/codeatlas-live
cd codeatlas-live
gh release download v0.1.0 --repo <owner>/codeatlas-live --pattern '*.nupkg' --dir artifacts/nuget
gh release download v0.1.0 --repo <owner>/codeatlas-live --pattern '*.tgz' --dir artifacts/npm
```

需要 .NET 10、Node 24；範例另需 Docker。安裝套件不代表自動啟動服務。

## 先跑範例

```bash
scripts/demo.sh
```

開啟終端列出的 app 與 viewer 網址，點 Create Order，再在 viewer 查看 interaction、
HTTP、endpoint/handler 和資料庫操作。選取 handler 查看來源，使用 Open in code-server 開檔。
點 out-of-stock 按鈕觀察失敗請求。Ctrl+C 停止範例。

遠端 code-server：

```bash
CODEATLAS_PUBLIC_BASE=https://code.example.com/absproxy scripts/demo.sh
```

替換成實際 code-server 位址；必須保留登入保護，不要公開 collector。

## 接入自己的專案

依照 [integration.md](integration.md) 操作：

1. 在既有 `nuget.config` 加入下載後的 `artifacts/nuget` 絕對路徑，不移除既有來源。
2. 僅 Debug 引用 `CodeAtlas.AspNetCore` 0.1.0，並設定 `CODEATLAS` compilation symbol。
3. 在 `#if CODEATLAS` 內匯入 namespace 並呼叫 `builder.AddCodeAtlas()`。
4. 視需求加入 Minimal API handler span；不要移除既有權限、驗證或 tracing。
5. 前端安裝 `artifacts/npm/codeatlas-web-0.1.0.tgz`，只在 development 初始化 runtime。
   僅代理 base-aware `codeatlas/v1/traces`，不可代理 collector 的管理 API。
6. 啟動 collector，`--source-root` 必須指向被觀察的 app，而不是 CodeAtlas repo。

```bash
dotnet tool install CodeAtlas.Collector --version 0.1.0 --tool-path ./artifacts/tool --add-source ./artifacts/nuget
./artifacts/tool/codeatlas --source-root /absolute/path/to/your-app
```

以上命令從 CodeAtlas repo 執行；預設 viewer 為 `http://127.0.0.1:4318/`。
套件已安裝時不要反覆 install，使用現有工具或依需要執行 `dotnet tool update`。

也可直接從原始碼執行：

```bash
dotnet run --project src/CodeAtlas.Collector -- --source-root /absolute/path/to/your-app
```

## example-app 已有的接入方式

example-app 使用 local ProjectReference，不需要改成套件引用。先在 CodeAtlas repo
執行 `scripts/pack-local.sh`，然後在 example-app repo 執行：

```bash
CODEATLAS_ROOT=/absolute/path/to/codeatlas-live ./script/dev-codeatlas.sh
```

launcher 管理 collector/backend/frontend；使用終端印出的網址。停用時取消
`CODEATLAS_ROOT` 並重新啟動正常開發流程。Dashboard 的既有開發設定會 opt-in，
所以若從 Dashboard 啟動也要檢查它的設定。

## 怎麼判讀、驗收

只能把實際 recorded span 當作執行證據；routing metadata、時間推測關聯與 stack trace
不是完整 method execution tree。未錄到的步驟要標示 unknown。
目前不自動記錄 handler 內每個 service/repository method。
SQL 預設只顯示操作與資料表，但 exception message、stack trace、自訂 tags 仍可能含敏感資料。

- 接入後跑專案既有測試，以及 Release build，確認 production 沒有 CodeAtlas runtime。
- 實際點 UI，確認新 trace、成功/失敗狀態、source 開檔和 code-server 路徑。
- 自動檢查：CodeAtlas repo 的 `scripts/test-all.sh`；人工項目見 [manual-checks.md](manual-checks.md)。
- viewer 空白時，先查 collector 是否啟動、Debug/Development 是否生效、代理與 base path、
  app 自己的 OpenTelemetry sampling；不要直接假設程式沒執行。

## 維護 Release

執行 `scripts/pack-local.sh` 後上傳 `.nupkg` 和 `.tgz` 到公開 repo Release。
版本改動需先同步各套件版本、文件與驗證，再建立新 tag；不要覆蓋已發布版本。
目前不使用 `dotnet nuget push` 或 `npm publish`；registry 發佈需另外明確授權。
