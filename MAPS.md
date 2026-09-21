# 打开迁入的两份地图

用 Unity 2022.3.54f1c1 打开这个项目，选择以下菜单后点击 Play：

- **Window > Hex Map > Open Flat Map**：平面地图，1100 × 469，882 个城市。
- **Window > Hex Map > Open Spherical Map**：原生球形地图，590,492 格，882 个城市、984 个辖区。

平面场景：`Assets/HexMapPackage/Scenes/Hex Map Scene.unity`。

球形场景：`Assets/StandaloneMaps/Scenes/Spherical Map.unity`。

详细使用、代码边界、备份和验证说明见 [StandaloneMaps/README](Assets/StandaloneMaps/README.md)。

两份地理数据独立保留，共享地形美术。球形地图提供浏览、城市、国界和格子选择；平面地图保留已有编辑笔刷。
