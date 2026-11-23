# Slay the Spire 2: 機制與逆向分析紀錄

> 分析基準：本機 Steam 安裝版 v0.113.1，build commit `d09d48e5`，檢視日期 2026-11-23。Early Access 會更新；文中的數值需隨遊戲版本重查。

## 範圍與方法

本研究使用 [REA](https://github.com/morluto/rea) 6.10.1 對本機遊戲檔案做唯讀靜態分析，搭配遊戲資料檔、官方／社群參考頁和兩個公開求解器的文件。沒有執行或修改遊戲，也沒有把 REA 的結構證據說成完整反編譯結果。

遊戲檔案：

- `data_sts2_windows_x86_64/sts2.dll`：10,648,064 bytes，PE/CLI、.NET 9、CIL-only；SHA-256 `62ecbe91093e1fa862de4329aac865209dcb297804caf0f0520d92d8c0b4a9c3`。
- `SlayTheSpire2.pck`：2,149,462,017 bytes，Godot 4.5.1 PCK v3。REA inventory 記錄 17,369 個項目；17,370 個檔案雜湊均驗證，沒有 mismatch 或 unavailable。
- 相鄰 `sts2.xml` 是 6 MB 的 C# XML 文件，提供類別與方法名稱，可作為挑選 REA 檢查目標的索引。

REA 的核心輸出顯示 `sts2.dll` 有 10,705 個 TypeDef、56,263 個 MethodDef。選取的核心型別做完整 metadata/CIL 成員盤點，包含 216 個 methods 和 854 條 call edges。完整證據 JSON 保存在工作區 `work/rea-research/`，未放入本 repo，以免提交約 70 MB 的暫存分析資料。

```powershell
node rea.mjs inspect-managed-artifact <path-to-sts2.dll> --detail summary --evidence_output <evidence.json> --json
node rea.mjs inspect-artifact <path-to-SlayTheSpire2.pck> --detail summary --evidence_output <evidence.json> --json
node rea.mjs inspect-managed-members <path-to-sts2.dll> `
  --type MegaCrit.Sts2.Core.Commands.PlayerCmd `
  --type MegaCrit.Sts2.Core.Commands.CreatureCmd `
  --type MegaCrit.Sts2.Core.Entities.Creatures.Creature `
  --type MegaCrit.Sts2.Core.Combat.CombatState `
  --type MegaCrit.Sts2.Core.Models.MultiplayerScalingModel `
  --type MegaCrit.Sts2.Core.MonsterMoves.Intents.AbstractIntent `
  --detail summary --evidence_output <members.json> --json
node rea.mjs inspect-managed-members <path-to-sts2.dll> `
  --type MegaCrit.Sts2.Core.Entities.Players.Player `
  --type MegaCrit.Sts2.Core.Commands.CardPileCmd `
  --type MegaCrit.Sts2.Core.Commands.CardCmd `
  --type MegaCrit.Sts2.Core.Entities.Cards.CardPile `
  --detail summary --evidence_output <piles.json> --json
```

實際安裝的 REA 套件是 npm `rea-agents@6.10.1`，以 Node 26.13.1 從隔離的工作目錄啟動。REA CLI 沒有寫入 Codex MCP 設定。

## 從程式結構得到的機制證據

| 主題 | REA 觀察到的程式位置 | 可以支持的結論 | 尚未支持的結論 |
|---|---|---|---|
| 能量 | `PlayerCmd.GainEnergy`, `LoseEnergy`, `SetEnergy`, `EndTurn` | 能量變化和結束回合是明確的玩家命令路徑；卡牌能量是玩家個別狀態 | 初始回合能量、特定角色或牌的數值，不能只由這些方法名稱推定 |
| 傷害、格擋、生命 | `CreatureCmd.Damage` overloads、`CreatureCmd.GainBlock`、`Creature.DamageBlockInternal`、`LoseHpInternal`、`GainBlockInternal`、`ClearBlock` | 傷害、格擋、扣 HP 與清除格擋分開處理；完整詳細資訊列在 REA 的 CIL anchors / call edges | 不能假定所有卡牌都只有直接傷害和格擋；狀態、遺物、附加觸發另有路徑 |
| 手牌／抽牌 | `CardPile.get_MaxCardsInHand` 的 CIL 是 `ldc.i4.s 10; ret`；另有 `CardPileCmd.Draw`, `DrawInternal`, `DrawWithoutBlockingOnOtherPlayers`, `Shuffle`, `ShuffleIfNecessary` | 本版手牌上限回傳 10；抽牌、洗牌和多人同步有各自命令 | `DrawInternal` 是 async wrapper；這個盤點沒有追完整 state machine，不能據此說明每個 draw 分支的順序 |
| 多人 HP 縮放 | `Creature.ScaleHpForMultiplayer`；`MultiplayerScalingModel.GetMultiplayerScaling`、`ModifyBlockMultiplicative` | 怪物 HP／格擋有多人 scaling 模型；單人玩家數不走多人縮放分支 | 精確倍率依 EncounterModel 和配置決定；工具不假設固定倍率，改用玩家輸入的目前 HP |
| 敵方意圖 | `AbstractIntent.GetIntentLabel`, `GetIntentDescription` | 意圖有可讀標籤和描述的取值路徑 | 靜態成員盤點不會代替當下遊戲畫面的意圖值或隨機狀態 |

REA 的 `inspect-managed-members` 可列出 PE/CLI metadata、method signatures、CIL anchors、call edges、field accesses 和載入的字串；不載入或執行遊戲程式。PCK inventory 驗證封裝格式和雜湊，但不解碼場景或 GDScript 遊戲邏輯。官方文件詳見 [Godot PCK guide](https://github.com/morluto/rea/blob/main/docs/godot-pck.md) 與 [managed code analysis guide](https://github.com/morluto/rea/blob/main/docs/guides/managed/managed-code-analysis.md)。

## 以公開資料補足的數值

公開的 [Draw Pile 規則頁](https://sts.wiki/keywords/draw-pile)記錄：回合開始通常抽 5 張，手牌上限 10，抽牌堆用盡會洗入棄牌堆。此為遊戲資料檔的二手整理，不是本次 REA CIL 盤點推出的結論。工具讓使用者手動輸入目前能量與手牌，避免把角色、牌、遺物或模式相關例外寫成固定值。

Glory Act 3 的三種 Boss 及參考生命值：

| Boss | 一般模式參考 HP | A8+ 參考 HP | 遭遇特性 |
|---|---:|---:|---|
| Aeonglass | 512 | 535 | Withering Presence 會把 Wither 放入抽牌堆；有 Artifact；Ebb 是 22 / 26 傷害並取得 33 格擋 |
| Queen | 400 | 419 | 和 Torch Head Amalgam 同場；守衛存活時 Queen 會取得 20 格擋，守衛倒下後 Queen 轉入攻擊循環 |
| Test Subject | 階段 1/2/3：100/200/300 | 111/212/313 | 三階段戰鬥；階段轉換會重置敵方生命狀態並帶入不同意圖 |

Queen 的 Torch Head Amalgam 基礎 HP 199，A8+ 為 211；開場 Strong Tackle 是 26 傷害，A9+ 為 32。以上根據 [Act 3: Glory](https://sts.wiki/acts/glory)、[Aeonglass](https://sts.wiki/enemies/aeonglass)、[Queen's Torch Head Amalgam](https://sts.wiki/enemies/torch-head-amalgam)、[Test Subject](https://sts.wiki/enemies/test-subject) 的版本資料整理。頁面提供「載入基準數值」，但多人時 HP scaling、當前生命、意圖和玩家狀態應以遊戲現況覆寫。

## 公開演算法專案比較

- [`rank-Yu/sts2-solver`](https://github.com/rank-Yu/sts2-solver)：README 將它定義為單人戰鬥 Mod，會讀取即時戰鬥狀態與 RNG，搜尋完整戰鬥路線並透過遊戲輸入出牌。它採用 CombatSolver 搜尋／估分思路和自製緊湊模擬核心；README 記錄 v0.113.1 的測試。Repo 顯示 MIT 授權。本研究借鑑「用狀態轉移加評分比較完整行動線」的設計，不複製其原始碼，也沒有把它直接整合到純 HTML 工具。
- [`zhiyue/sts2-rl-agent`](https://github.com/zhiyue/sts2-rl-agent)：提供 headless Python 戰鬥模擬器、Gymnasium 環境、MaskablePPO 訓練流程與 C# 遊戲橋接。README 主述單一 agent；目前公開說明沒有提供協調多人手牌的政策。Repo 頁面未列出 LICENSE，因此本研究只讀取文件並引用其架構，不重用程式碼。

兩個專案和本工具的目標不同：前者在遊戲中直接搜索並執行；後者訓練策略；本工具則是可看見計分準則的單回合組合搜尋，方便課堂展示與人工操作。

### `rank-Yu/sts2-solver` 的狀態取得方式

為確認它能否作為「遊戲外讀記憶體」的參考，本研究唯讀檢視了上游原始碼。結果是它**不是解耦的外部讀取器**：`src/Mod/Entry.cs` 使用 `MegaCrit.Sts2.Core.Modding` 與 RitsuLib 初始化遊戲 Mod、註冊 Harmony 類型的補丁和遊戲生命週期回呼；`CombatSessionController` 從遊戲回呼取得原生 `CombatState`，再用 `LocalContext.GetMe(state)` 取得玩家物件。`CombatSessionController.Search.cs` 呼叫 `LiveCombatTracker.ImportRoot(_state, _player, _rules)`，把原生物件轉成求解器自己的 `Battle` 狀態。其後求解在模擬器狀態上進行，執行功能再透過遊戲原生操作入口下指令。

原始碼中這條路徑依賴 Mod 載入、遊戲內物件參照和回呼；沒有找到 `OpenProcess`、`ReadProcessMemory` 或獨立 companion process 的記憶體讀取實作。README 也將它定義為單人戰鬥 Mod；`CombatSessionController.Search.cs` 對多人場呼叫 `IsMultiplayerSession` 後直接拒絕搜尋。因此它的模擬狀態、戰鬥搜索和驗證流程值得作演算法參考，但狀態匯入層不能直接滿足本專案的免 Mod／單人與合作需求。

## 遊戲外即時讀取

Windows companion 使用 ClrMD 的程序快照與遊戲附帶的 .NET 9 DAC。讀取器檢查 v0.113.1 的組件雜湊，追蹤記憶體中的戰鬥管理器、回合狀態和場上角色。它分讀每位玩家的 `PlayerCombatState` 與 `Hand._cards`。唯讀 HTTP 服務只在本機回送地址提供頁面與版本化快照。

單人實測第一回合讀到玩家 64/80 HP、0 格擋、3 能量、5 張手牌，包含兩張各自獨立的打擊。敵人 Shrinker Beetle 為 40/40 HP，其 `SHRINKER_MOVE` 意圖沒有攻擊。快照亦包含卡牌型別、升級數、基礎費用，以及可辨識的基礎傷害與格擋變數。這些基礎變數不包含目標與戰鬥狀態造成的修正。

外部讀取可以取得遊戲客戶端實際持有的物件。僅憑程式定義有手牌欄位，無法保證遠端玩家的值已同步到這一台客戶端。雙人手牌與能量仍需以兩邊遊戲畫面交叉驗證。參考 [ClrMD 說明](https://github.com/microsoft/clrmd/blob/main/doc/GettingStarted.md)。

## 單人跨回合讀取

同一份程序快照現在也讀取 `PlayerCombatState.DrawPile`、上限能量、進階值、卡牌修飾標記、目前能力與遺物，以及敵人的 `MoveState.FollowUpState`。REA 在 `CardPile.MoveToTopInternal` 讀到把牌插入陣列位置 0 的指令，因此抽牌堆由索引 0 開始取牌。模型用標準的 5 張抽牌規則，並在牌堆不足時停止預測。

本機第一場縮小甲蟲戰的快照包含 `SHRINKER_MOVE → CHOMP_MOVE → STOMP_MOVE → CHOMP_MOVE` 以及 6 張未抽牌。REA 顯示 A9 以下咬擊／踩踏的基礎傷害是 7／13，A9 起是 8／14，縮小令有力量標記的攻擊傷害乘以 0.7。痛擊的 CIL 顯示基礎易傷 2 層，升級會增加 1 層。易傷的基礎倍率是 1.5。模型只套用已知的組合，遇到其他能力、敵人或修飾就停止對相應回合求解。


## 標準與進階地圖

REA 觀察到 `RunState._visitedMapCoords` 最後一筆是目前地圖座標。`ActMap.startMapPoints` 提供起點，`MapPoint.Children` 提供有方向的合法下一站，`MapPointType` 區分戰鬥、精英、營火、商店、寶箱、未知房間、先古之民與 Boss。Windows 讀取器只取節點位置、類型和連線，並使用當前 HP 和金幣排序。

進階 2 的第一章實機快照讀到 68/80 HP、117 金幣。從座標 `1:2` 只有 `2:2` 可走，與遊戲畫面一致。進入 `2:2` 後，該節點有兩條向上分支。座標使用從 0 開始的列與欄，頁面把欄轉成從 1 開始。
