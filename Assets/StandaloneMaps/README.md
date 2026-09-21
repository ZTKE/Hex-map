# 独立地图：平面与球形

本次地图从 `D:/ww2_new2` 的 2026-09-19 工作区迁入 `D:/hex-map`。
使用 Unity **2022.3.54f1c1 / URP 14.0.11** 打开 hex-map 项目。

## 打开与运行

| 地图 | 菜单 | 场景 |
| --- | --- | --- |
| 平面地图 | Window > Hex Map > Open Flat Map | `Assets/HexMapPackage/Scenes/Hex Map Scene.unity` |
| 球形地图 | Window > Hex Map > Open Spherical Map | `Assets/StandaloneMaps/Scenes/Spherical Map.unity` |

打开场景后点击 Play。平面地图沿用原有编辑器、城市层、国家数据和地形笔刷。
地形、森林与高度配方通过 **Window > Hex Map > Terrain Definitions** 编辑。

球形地图使用独立球面相机：滚轮缩放，WASD / 方向键移动，Q/E 旋转，拖动平移，点击选择格子。
原有球面预览面板提供区域预设与格网显示；底部新增地图数据状态和选中格子的国家、辖区、城市信息。
城市显示使用原游戏的标记和标签实现，远距离自动隐藏；国家边界使用保存的球形交互数据。

## 两份独立地理数据

- 平面地图：`Assets/HexMapPackage/Resources/Maps/DefaultWorld.bytes`，v12，1100 × 469，共 515,900 格、882 个城市。
- 球形地图：`Assets/StreamingAssets/SphericalMap`，590,492 格、882 个城市、984 个辖区。
- 球形的 `TerrainR5.bytes`、`Gameplay-v1.bin`、`StartupR5.bytes`、`Interaction-v1.bin` 和 `manifest.json` 成套保存，并保持源文件 SHA-256 一致。
- 平面的地形、河流、国家、城市导入源以及 `Assets/Resources/Data/Map/WorldRegions.bytes` 等数据一起保留。

球形场景直接加载保存的原生球面，不创建平面 HexGrid，不依赖 GameManager，不在每次启动时重新导入城市。
球形国家调色板在本次迁移时单独保存于 `SphereCountryPalette.cs`。后续编辑平面地图不改变球形地图的地理或调色板。
两份地图共享地形美术和材质配方，因此修改共享美术可能影响两者的外观。

球形场景保留浏览、城市、国界和格子选择；原平面笔刷没有转换为球面编辑笔刷。
战争、军队、经济和游戏 UI 属于 ww2_new2 的游戏系统，不属于这次独立地图入口。
如需编辑球形地理数据，应基于原生球面 ID 和配套保存格式扩展，不要把旧平面地图重新烘焙覆盖它。

## 代码与资源边界

- `Assets/HexMapPackage`：当前平面地图和共享地形实现，按相对路径及哈希迁移。
- `Assets/MapProjectionIntegration/SphericalTerrainPreview`：当前球面地形、植被、水、LOD、相机和预览工具。
- `Assets/IcoSphere`：球面拓扑数据结构及 R3/R4/R5 网格资源；未导入旧 IcoSphere 演示控制器。
- `Assets/StandaloneMaps`：独立球形场景、入口菜单、从原游戏提取的数据读取、国界、城市显示，以及验证工具。
- `Assets/MapProjectionIntegration/SatellitePreview` 中只迁移球形场景引用的图片；中文字体也按真实引用迁移。

独立球面数据/国界/城市类使用 `ZTKE.HexMap.Standalone` 命名空间，与源项目游戏程序集隔离。
`Assets/MapProjectionIntegration/SphericalTerrainPreview/Scenes/Spherical Terrain Preview.unity` 仍是原有地形展示参考，完整独立地图请使用上表的 `Spherical Map.unity`。

## 备份与验证

目标项目本来处于尚未提交的目录整理状态。迁移没有执行 git reset、clean、checkout 或删除已有文件。
45 个被替换文件的原版本位于 `Artifacts/MapMigration20260919/Before/Assets/...`；复制清单、原始哈希和修改前 Git 状态在同级目录。
项目的 Packages、ProjectSettings 和构建场景列表沿用 hex-map 自己的配置，没有复制游戏项目设置。

文件与场景验证报告及截图在 `Artifacts/MapMigration20260919`。
命令行验证入口为 `ZTKE.HexMap.Standalone.Editor.MapMigrationValidation.Run`；它依次运行球形和平面场景，检查保存数据和地图统计，完成后退出该验证用 Unity 进程。
该入口会重新生成独立球形场景；已有手工场景修改时不要调用，应直接打开已保存场景。

迁移后的兼容性修复：`HexOverviewAtmosphereFeature` 和 `HexGlobeAtmosphereFeature` 的渲染 Pass 显式请求颜色输入。
源游戏使用强制中间颜色纹理，hex-map 使用自动模式；此声明让 URP 自动提供可采样的颜色纹理，避免截图或中景渲染出现空纹理异常。
这两处目标项目代码因此与源共享包存在有意差异，修改前版本在 `Artifacts/MapMigration20260919/BeforeAdapterFix`。

历史遗留引用：平面场景序列化文本含一个已不再使用的 `cellPrefab` 字段；未被场景使用的 `Hex Map Scene_Profiles/Global Volume Profile.asset` 引用旧 Post Processing 包。这些在迁移前已存在，保留原样，不是本次新引入的运行时依赖。
