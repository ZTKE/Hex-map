# Game_2 球面主地图

这组文件保存实际球面地图，正常进入 Game_2 直接读取。城市和地形不再每次从平面世界迁移。

| 文件 | 内容 |
| --- | --- |
| TerrainR5.bytes | 590,492 球面格的中心、角点、邻接、完整地形/植被属性、河流路径和空间查询索引 |
| Gameplay-v1.bin | 初始国家归属、玩法地形、882 城市、984 辖区、河流通行关系、旧脚本格 ID 的一次性映射 |
| StartupR5.bytes | 全球背景地形/海面网格及分块索引 |
| Interaction-v1.bin | 球格检索纹理、边界平面及初始国界纹理 |
| manifest.json | 文件大小和 SHA-256；Gameplay 与 Terrain 另外绑定同一地图内容 ID |

近景细节仍按镜头位置加载，运行中的战争、经济、选中状态与部队状态不属于这份初始地图。

在地图就绪后，通过 `Window > Map Projection > Game 2 Sphere > Save Native Map and Startup Data` 保存。保存流程先在 Temp 暂存并回读校验，完成后备份旧文件并发布新文件。地形和玩法文件须成组保留。地图内容或格式不匹配会明确报错；外观配方改变时背景网格会重建，重新保存后恢复快速加载。

平面地图仍保留在原目录；额外备份和恢复说明位于项目下 `Artifacts/MapBackups/20260914_225819_FlatMapBeforeSpherePrimary`。修改平面地图不会自动覆盖这份球面主数据。

运行验证：`Tools/SphericalGameIntegration/SendReview.ps1` 的 `play`、`bake`、`validate`、`stop` 操作；`WaitReview.ps1` 接收完成通知。文件路径用于当前桌面平台的 StreamingAssets。
