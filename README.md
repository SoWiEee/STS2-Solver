# STS2 Solver

《Slay the Spire 2》免 Mod 戰鬥顧問。Windows 讀取器取得遊戲當前的手牌、生命、能量、抽牌堆和敵方意圖。網頁自動計算單人出牌路線，顯示現在先打哪張牌、攻擊誰，以及後續各回合的預測結果。玩家在遊戲中自行出牌。

> [!NOTE]
> 即時求解已在 **v0.113.1** 的縮小甲蟲、三史萊姆、小啃獸和墨寶戰對照遊戲畫面。未知洗牌最多估算一次，實際抽牌後會從快照重新求解。Aeonglass 目前涵蓋兩種攻擊與格擋，完整 Boss 戰和單人通關仍待驗證。

## 啟動

需要 Windows x64、.NET 9 SDK 和 Steam 版 STS2 v0.113.1。初次 clone 後先取得子模組，再在專案目錄執行：

```powershell
git submodule update --init
dotnet run --project reader
```

1. 開啟 [本機戰術工作台](http://127.0.0.1:8765/)。
2. 在遊戲進入支援的單人戰鬥，等到可出牌。網頁會在前景每 5 秒嘗試取得新的快照，局面有變才重新求解。
3. 看「現在先打出」與下方出牌順序。擷取會在你切回網頁後更新。需要保留畫面時可按「暫停讀取」。

初次啟動會從 NuGet 還原 ClrMD。工作台經由本機回送介面接收資料。若顯示「目前的遊戲組件版本未支援」，請確認正在執行的 STS2 是文件所列的組件版本。

## 地圖路線

單人標準／進階模式可按「讀取地圖」，查看從當前房間到章節 Boss 的推薦與備選。評分使用目前生命與金幣，供玩家比較精英、營火和商店的取捨。是否使用力量藥水會列入戰鬥比較。

## 獎勵建議

在遊戲開啟戰後卡牌選擇、含遺物的戰利品畫面，或開啟寶箱並停在領取前，再按工作台的「讀取獎勵」。可辨識的傷害、格擋、抽牌與開戰回復效果會依牌組和當下生命評分。能略過的卡牌獎勵會顯示略過選項，未建模的效果會標記為未評估。已實機對照卡牌選擇與寶箱的小血瓶。

## 推薦如何計算

- 每回合比較可支付的牌、可選目標與出牌順序。痛擊的易傷能改變同回合與下回合的後續傷害。
- 搜尋最多四個回合。已知牌堆按實際順序抽牌。若需要洗牌，估算最多一次，並比較三組樣本的當前回合路線。樣本不一致時優先回到可確定的單回合建議。未知敵招仍會縮短預測。
- 路線優先選擇擊敗敵人，其次比較存活、剩餘生命及敵人生命損失。達到搜尋預算時，頁面會標明推薦是已搜尋到的最高分路線。
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
