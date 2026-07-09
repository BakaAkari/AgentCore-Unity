# Unity Game Architecture Blueprints

> **Purpose**: Provide lightweight architecture skeletons for new game projects or vertical slices, avoiding decision paralysis when starting from scratch.
>
> **Applicable Scope**: New project startup, prototype building, small game architecture design, vertical slice planning.
>
> **Trigger Scenarios**: User says "make a game", "start from scratch", "build a prototype", "how to design a game framework", etc.

---

## 1. Core Principles

1. Provide a minimum viable skeleton, not a large reusable framework
2. Keep the script list short — no template bloat for "things we might need in the future"
3. Get the core loop running first, then consider extensions

---

## 2. Blueprint Templates

### 2.1 2D Platformer

**Core Loop**: Move → Jump → Collect → Reach the goal

| Module | Responsibility | Key Scripts |
|--------|---------------|-------------|
| Player | Movement, jumping, state | `PlayerController`, `PlayerState` |
| Level | Level loading, checkpoints | `LevelManager`, `Checkpoint` |
| Pickup | Collectible logic | `Collectible`, `ScoreTracker` |
| Hazard | Damage, death, respawn | `DamageZone`, `RespawnHandler` |
| UI | HUD, pause, results | `HUDView`, `PauseMenu` |

**Scene Plan**: `MainMenu` → `GameLevel_01..N` → `GameOver`

**Data Assets**: Level configuration via ScriptableObject, player saves via JSON/PlayerPrefs

### 2.2 Top-Down Shooter

**Core Loop**: Move → Aim → Shoot → Enemy waves → Survive

| Module | Responsibility | Key Scripts |
|--------|---------------|-------------|
| Player | Movement, aiming, shooting | `PlayerMovement`, `WeaponSystem` |
| Enemy | AI, spawning, death | `EnemyAI`, `EnemySpawner` |
| Projectile | Bullets, collision, object pooling | `Bullet`, `ProjectilePool` |
| Wave | Wave management | `WaveManager`, `WaveConfig`(SO) |
| UI | Health, ammo, wave indicators | `HUDView`, `DamageFlash` |

**Key Decisions**: Bullets must use object pooling; enemy AI uses a simple state machine (Idle/Chase/Attack)

### 2.3 Endless Runner

**Core Loop**: Auto-advance → Dodge obstacles → Collect → Accelerate → Die

| Module | Responsibility | Key Scripts |
|--------|---------------|-------------|
| Runner | Auto-movement, lane switching/jumping | `RunnerController`, `InputHandler` |
| Track | Tile generation, recycling | `TrackSpawner`, `TrackSegment` |
| Obstacle | Obstacle configuration | `ObstacleConfig`(SO), `ObstacleSpawner` |
| Score | Distance, coins | `ScoreManager`, `CoinPickup` |
| UI | Score, revive, leaderboard | `RunHUD`, `GameOverPanel` |

**Key Decisions**: Track tiles use object pool recycling; difficulty curve configured via ScriptableObject

### 2.4 Puzzle / Interactive

**Core Loop**: Explore → Discover clues → Solve puzzles → Advance the story

| Module | Responsibility | Key Scripts |
|--------|---------------|-------------|
| Player | Movement, interaction | `PlayerController`, `Interactor` |
| Puzzle | Puzzle logic | `PuzzleBase`, `SwitchPuzzle`, `SequencePuzzle` |
| Inventory | Item management | `Inventory`, `ItemData`(SO) |
| Dialogue | Dialogue system | `DialogueRunner`, `DialogueData`(SO) |
| UI | Inventory panel, dialogue box | `InventoryUI`, `DialogueBox` |

**Key Decisions**: Puzzles unified via `IPuzzle` interface; item data uses ScriptableObject

### 2.5 Tower Defense

**Core Loop**: Place towers → Enemies advance along path → Towers attack → Wave ends

| Module | Responsibility | Key Scripts |
|--------|---------------|-------------|
| Tower | Placement, upgrades, attacks | `TowerPlacer`, `TowerBase`, `TowerData`(SO) |
| Enemy | Follow path, take damage | `PathFollower`, `EnemyHealth` |
| Path | Path definition | `WaypointPath` |
| Wave | Wave configuration | `WaveManager`, `WaveConfig`(SO) |
| Economy | Currency, costs | `CurrencyManager` |
| UI | Tower selection, wave info | `TowerShopUI`, `WaveHUD` |

**Key Decisions**: Tower data uses ScriptableObject; enemy paths use Waypoint arrays

### 2.6 Clicker / Idle

**Core Loop**: Click to produce → Upgrade → Automate → Unlock new content

| Module | Responsibility | Key Scripts |
|--------|---------------|-------------|
| Core | Resource production, multipliers | `ResourceManager`, `ClickHandler` |
| Upgrade | Upgrade tree | `UpgradeData`(SO), `UpgradeManager` |
| Prestige | Reset loop | `PrestigeSystem` |
| Save | Offline earnings, save data | `SaveManager`, `OfflineCalculator` |
| UI | Resource display, upgrade buttons | `ResourceHUD`, `UpgradePanel` |

**Key Decisions**: Large numbers use `double` or custom BigNumber; saves use JSON serialization

### 2.7 Card Game / Turn-Based

**Core Loop**: Draw cards → Play cards → Resolve effects → Opponent's turn

| Module | Responsibility | Key Scripts |
|--------|---------------|-------------|
| Card | Card data, effects | `CardData`(SO), `CardEffect` |
| Deck | Deck, draw, discard | `DeckManager`, `HandView` |
| Battle | Turn flow, win/loss | `BattleManager`, `TurnStateMachine` |
| Target | Target selection | `TargetSelector` |
| UI | Hand, battlefield, health | `CardUI`, `BattleHUD` |

**Key Decisions**: Card data uses ScriptableObject; turn flow uses state machine; effects use strategy pattern

---

## 3. Standard Output Format

When providing a blueprint for any game type, output in the following structure:

```markdown
## Core Loop
One sentence describing the player's main action chain.

## Recommended Scenes
List 2-5 scenes and their responsibilities.

## Module List
3-7 modules, each with a one-line responsibility description.

## Initial Script List
List key script names by module, no more than 15 total.

## Data Assets
Which data uses ScriptableObject, which uses plain C# classes.

## UI Responsibilities
List the required UI panels.

## Deliberately Kept Simple
Explicitly list things that are "not doing now".
```

---

## 4. Guardrail Rules

- Provide a minimum viable blueprint, not a massive reusable framework
- Keep the script list under 15 items
- Do not pre-build abstraction layers for "things we might need in the future"
- Do not introduce DI frameworks, ECS, or complex event buses at the prototype stage
- If the user hasn't specified the game type, ask before providing a blueprint

---

## 5. Related Skills

- Pattern selection → refer to `unity-patterns`
- Scene assembly → refer to `unity-scene-contracts`
- Script writing → refer to `unity-runtime-dev`
- Performance considerations → refer to `unity-performance-analysis`
