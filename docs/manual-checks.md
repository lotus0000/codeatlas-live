# Manual checks

Checks a person makes before calling a version done. Everything else runs in `scripts/test-all.sh`.

| # | Check | How | Status |
|---|---|---|---|
| 1 | **code-server: "Open in code-server" opens the file at the declaration line, in the window you are using** | From a code-server terminal: `CODEATLAS_PUBLIC_BASE=https://<your-code-server>/absproxy scripts/demo.sh`. Open both URLs, click **Create Order**, select the handler, click **Open in code-server**. The editor shows `samples/MinimalApiSample/Orders/OrderEndpoints.cs` with the cursor on `CreateOrder`'s declaration line. | confirmed by the maintainer (2026-10-11) |
| 2 | The viewer reads well in light and dark | Switch the theme button through system → light → dark with a trace selected; nothing is unreadable or invisible. | confirmed by the maintainer (2026-10-11) |
| 3 | The whole demo flow, by a person | Create Order, Create Order (out of stock), Create Order (Axios), Refresh products: each appears in the viewer as described in README "See it in 2 minutes". | confirmed by the maintainer (2026-10-11) |
| 4 | VS Code desktop | Collector with `--editor vscode` on a machine with VS Code; "Open in VS Code" opens the file at the line. | not run (no desktop VS Code here) |
| 5 | Rider on Windows | Collector with `--editor rider --editor-command "C:\…\rider64.exe"`; "Open in Rider" opens the file at the line. | not run (no Windows here) |
