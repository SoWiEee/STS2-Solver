# STS2 Solver

《Slay the Spire 2》免 Mod 戰鬥顧問。Windows 讀取器從遊戲程序的記憶體快照取得當前戰鬥，組成 [rank-Yu/sts2-solver](https://github.com/rank-Yu/sts2-solver) 求解核心的輸入，搜尋到戰鬥結束，網頁顯示每回合出牌、目標與選擇，以及預測的戰鬥結束生命。玩家在遊戲中自行出牌；讀取器不修改遊戲。

> [!NOTE]
> 已在 **v0.113.1** 進階 2 實機對照：Corpse Slug、Seapunk 與精英 Skulking Colony 照建議路線打完，戰鬥結束生命都與預測相同。目錄外敵人、進階 9 以上與 Boss 戰仍待更多實機驗證；支援範圍與驗證紀錄見 [docs/live-import.md](docs/live-import.md)。

## 啟動

需要 Windows x64、.NET 9 SDK 和 Steam 版 STS2 v0.113.1。初次 clone 後先取得子模組，再在專案目錄執行：

```powershell
git submodule update --init
dotnet run --project reader
```

1. 開啟 [本機戰術工作台](http://127.0.0.1:8765/)。
2. 在遊戲進入單人戰鬥，等到可出牌。網頁在前景每 5 秒取得快照，局面有變就自動執行「整場戰鬥出牌建議」。
3. 依「本回合」的出牌順序、目標與選擇在遊戲中出牌。不支援的局面會列出原因，此時可參考下方「簡易模型（備用）」。需要保留畫面時可按「暫停讀取」。

不想切換視窗時，另開一個終端機執行置頂小視窗（需 WebView2 執行環境，Windows 11 已內建）：

```powershell
dotnet run --project tools/overlay
```

它預設在螢幕左側（避開右側的結束回合與繼續按鈕）、永遠在遊戲上方，移動或縮放後會記住位置，戰鬥中顯示本回合出牌，戰鬥外每 4 秒自動刷新獎勵、商店、營火與事件建議。遊戲需以視窗或無邊框模式執行。

同一局面的搜尋結果會在讀取器內共用；照建議出牌時，網頁只把路線往前推一步，只有抽到新牌、回合變化或局面與預測不同時才重新搜尋。一般戰鬥的搜尋上限為 8 秒，菁英與首領為 30 秒。

初次啟動會從 NuGet 還原 ClrMD。工作台經由本機回送介面接收資料。若顯示「目前的遊戲組件版本未支援」，請確認正在執行的 STS2 是文件所列的組件版本。

## 地圖路線

單人標準／進階模式可按「讀取地圖」，查看從當前房間到章節 Boss 的推薦與備選。評分使用目前生命與金幣，供玩家比較精英、營火和商店的取捨。是否使用力量藥水會列入戰鬥比較。

## 獎勵建議

在遊戲開啟戰後卡牌選擇、含遺物的戰利品畫面，或開啟寶箱並停在領取前，再按工作台的「讀取獎勵」。遺物名稱和效果說明讀自本機遊戲的繁中資料。評分涵蓋傷害、格擋、抽牌、開戰回血、首回合能量、力量、敏捷和活力。其餘遺物仍顯示效果說明，並標記為未評估。

整場戰鬥模型會計入核心已支援的卡牌附魔與苦難。遇到核心無法辨識的遺物、附魔或苦難時，保留可讀取的戰鬥狀態繼續計算，並在結果列出略過的效果。執行建議前可在遊戲確認目前費用、傷害和可選目標。

## 推薦如何計算

- **整場戰鬥出牌建議**（主要）：rank-Yu 的束搜尋從目前局面一路模擬到戰鬥結束，比較勝負、戰鬥結束生命與長期收益；敵人行動模式、亂數與抽牌順序都按遊戲實際狀態模擬。規則與評分表的來源見 [docs/live-import.md](docs/live-import.md)。搜尋不保證全域最佳，從不同回合重新搜尋可能得到不同路線。
- **簡易模型（備用）**：本專案原本的 JS 多回合模型，最多四回合、只涵蓋少數敵人，細節見 [docs/policy.md](docs/policy.md)。
- 工作台只讀取執行中的遊戲；原本手動輸入的離線單回合沙盒已移除。

## 專案結構

| 檔案 | 用途 |
| --- | --- |
| [reader/Program.cs](reader/Program.cs) | 建立程序快照並提供本機讀取介面 |
| [live-policy.js](live-policy.js) | 把支援的即時快照轉成可計算的回合 |
| [map-policy.js](map-policy.js) | 章節地圖評分與路線排序 |
| [reward-policy.js](reward-policy.js) | 卡牌與已知遺物效果的啟發式評分 |
| [multi-turn.js](multi-turn.js) | 跨回合搜尋與排名 |
| [index.html](index.html) | 即時建議畫面 |
| [importer/](importer/) | 從程序快照組出 rank-Yu 求解器的 `Battle`／`Rules`（[說明](docs/live-import.md)） |
| [tools/rules-catalog/](tools/rules-catalog/) | 由上游凍結規則表產生 `reader/data/rules-catalog.br`（[涵蓋率](docs/rules-catalog.md)） |
| `vendor/sts2-solver` | rank-Yu/sts2-solver 子模組（MIT，見 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)） |
| [docs/game.md](docs/game.md) | REA 逆向分析、版本與實機證據 |
| [docs/policy.md](docs/policy.md) | 模型範圍、搜尋規則和報告實驗方向 |
