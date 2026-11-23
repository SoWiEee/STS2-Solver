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

初次啟動會從 NuGet 還原 ClrMD。工作台經由本機回送介面接收資料。若顯示「目前的遊戲組件版本未支援」，請確認正在執行的 STS2 是文件所列的組件版本。

## 地圖路線

單人標準／進階模式可按「讀取地圖」，查看從當前房間到章節 Boss 的推薦與備選。評分使用目前生命與金幣，供玩家比較精英、營火和商店的取捨。是否使用力量藥水會列入戰鬥比較。

## 獎勵建議

在遊戲開啟戰後卡牌選擇、含遺物的戰利品畫面，或開啟寶箱並停在領取前，再按工作台的「讀取獎勵」。可辨識的傷害、格擋、抽牌與開戰回復效果會依牌組和當下生命評分。能略過的卡牌獎勵會顯示略過選項，未建模的效果會標記為未評估。已實機對照卡牌選擇與寶箱的小血瓶。

## 推薦如何計算

- **整場戰鬥出牌建議**（主要）：rank-Yu 的束搜尋從目前局面一路模擬到戰鬥結束，比較勝負、戰鬥結束生命與長期收益；敵人行動模式、亂數與抽牌順序都按遊戲實際狀態模擬。規則與評分表的來源見 [docs/live-import.md](docs/live-import.md)。搜尋不保證全域最佳，從不同回合重新搜尋可能得到不同路線。
- **簡易模型（備用）**：本專案原本的 JS 多回合模型，最多四回合、只涵蓋少數敵人，細節見 [docs/policy.md](docs/policy.md)。
- 目前遊戲畫面供實戰使用。直接開啟 `index.html` 則是課堂用的離線單回合沙盒，包含單人及雙人輸入，可用於比較假設。

## 專案結構

| 檔案 | 用途 |
| --- | --- |
| [reader/Program.cs](reader/Program.cs) | 建立程序快照並提供本機讀取介面 |
| [live-policy.js](live-policy.js) | 把支援的即時快照轉成可計算的回合 |
| [map-policy.js](map-policy.js) | 章節地圖評分與路線排序 |
| [reward-policy.js](reward-policy.js) | 卡牌與已知遺物效果的啟發式評分 |
| [multi-turn.js](multi-turn.js) | 跨回合搜尋與排名 |
| [index.html](index.html) | 即時建議畫面與離線沙盒 |
| [importer/](importer/) | 從程序快照組出 rank-Yu 求解器的 `Battle`／`Rules`（[說明](docs/live-import.md)） |
| [tools/rules-catalog/](tools/rules-catalog/) | 由上游凍結規則表產生 `reader/data/rules-catalog.br`（[涵蓋率](docs/rules-catalog.md)） |
| `vendor/sts2-solver` | rank-Yu/sts2-solver 子模組（MIT，見 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)） |
| [docs/game.md](docs/game.md) | REA 逆向分析、版本與實機證據 |
| [docs/policy.md](docs/policy.md) | 模型範圍、搜尋規則和報告實驗方向 |
