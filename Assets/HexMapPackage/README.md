# Hex Map Package

这是从提交 `9f3907f Add migrated world cities and overview rendering` 整理出来的
完整平面六边形地图系统。资源内容没有做极限精简，只是在原有目录外增加了
统一的 `Assets/HexMapPackage` 父目录，便于迁移到其他 Unity 项目。

## 导出

在 Unity 的 Project 窗口中选中：

`Assets/HexMapPackage`

然后右键选择 **Export Package...**，保持 **Include dependencies** 勾选，导出为
一个 `.unitypackage` 文件即可。不要选整个 `Assets` 或整个项目。

## 导入后使用

1. 目标项目使用 Unity 2022.3 LTS，并安装 Universal Render Pipeline 14。
2. 通过 **Assets > Import Package > Custom Package...** 导入。
3. 打开 `Assets/HexMapPackage/Scenes/Hex Map Scene.unity`。
4. 运行场景，默认会加载已经烘焙好的平面世界地图；手动新建地图时才会替换它。

城市层已经在场景的 `Hex Grid/World Cities` 下，并挂载 `HexCityLayer`，不需要
额外创建组件。默认地图数据在
`Assets/HexMapPackage/Resources/Maps/DefaultWorld.bytes`。

如果目标项目没有自己的 URP 配置，可以在 **Project Settings > Graphics** 中
使用 `Assets/HexMapPackage/URP/URP Settings.asset`。目标项目已有 URP 配置时，
通常继续使用自己的配置即可。

## 目录

- `Scripts`：地图运行时、编辑器 UI、城市、国家数据结构。
- `Editor`：默认世界地图和城市数据的重新烘焙工具。
- `Materials`：地形、河流、海洋、国界、城市等材质与 Shader。
- `Prefabs`：网格块、地块标签、单位和地形装饰预制体。
- `Resources`：启动时自动加载的默认地图及政治图数据。
- `Scenes`：已经配置好的完整演示/编辑场景。
- `MapData`：球形项目迁移过来的国家、城市和顶点数据源。
- `ThirdParty`：HoneyFramework 原始美术资源及说明。
- `URP`：项目使用的 URP Renderer、Pipeline 和全局设置资源。

包内运行时代码与编辑器工具使用独立 Assembly Definition，并且不会自动
暴露给目标项目的默认 Assembly-CSharp。这样可以与目标项目中已有的旧版
`HexMetrics`、`HexCoordinates` 等同名类型共存。
