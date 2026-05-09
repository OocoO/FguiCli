# FGUI Package Publisher

Unity 编辑器内的 FairyGUI 发布脚本：`Assets/Scripts/Editor/FguiPackagePublisher.cs`

## 作用
从 FairyGUI 源包目录生成发布产物：
- `<PackageName>.bytes`
- `<PackageName>@sprites.bytes`
- `<PackageName>@atlas{index}.png`

## 已实现规则
- 组件内容写入打包文本容器，条目格式：`filename|length|content`
- 容器条目按文件名排序，包含 `package.xml`
- `@sprites.bytes` 行格式：`resourceId atlasIndex x y w h rotated`
- `atlasIndex` 使用 `package.xml` 中 atlas 数字后缀
- `rotated=1` 时，精灵写入图集时做 90 度旋转，`w/h` 为旋转后的矩形尺寸
- 组件 XML 发布时会移除 XML 声明、`fileName`、`bgColor`、`exported`（子节点上）以及 `<remark>`
- 发布 `package.xml` 时：
  - 资源名去掉 `.xml` / 图片扩展名
  - 为组件和图片补 `size`
  - 保留额外资源属性（例如 `scale`、`scale9grid`、`branch`、`branches`）
  - atlas 节点按 atlas 索引输出

## 包含策略
- 以 `exported="true"` 的资源作为发布入口
- 递归分析组件 XML 中的 `src="..."` 与 `ui://<pkgId><resId>` 引用
- 自动补齐依赖的组件与图片

## 菜单使用
Unity 菜单：
- `Tools/Fgui Package/Publish Folder...`

先选源包目录（需要包含 `package.xml`），再选输出目录。

## 发布模式
- `LegacyTextContainer`（默认）：输出自定义文本容器（`<PackageName>.bytes`、`@sprites.bytes`、`@atlas*.png`）
- `OfficialRuntime`：用于渲染链路，要求输出目录里有官方运行时描述文件 `*_fui.bytes`

`OfficialRuntime` 的执行顺序：
1. 如果输入目录本身已经是官方发布目录（含 `*_fui.bytes`），直接拷贝到输出目录
2. 否则（仅 Unity Editor）尝试执行环境变量 `FGUI_OFFICIAL_PUBLISH_CMD` 指定的外部发布命令
3. 若仍未产出 `*_fui.bytes`，发布失败

命令模板支持占位符：`{sourceDir}`、`{outputDir}`。

## 命令行使用
```powershell
& 'C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Unity.exe' `
  -projectPath 'D:\Project\FguiCli' `
  -batchmode `
  -quit `
  -executeMethod Editor.FguiPackagePublisher.PublishFromCommandLine `
  --packageDir 'D:\ProjectGit\AirLegion\fgui_airLegion\assets\BattleUI' `
  --outputDir 'D:\Project\FguiCli\Build\Published\BattleUI' `
  --mode legacy
```

官方 runtime 模式（依赖外部 FairyGUI 官方导出命令）：
```powershell
$env:FGUI_OFFICIAL_PUBLISH_CMD = '"C:\Path\To\FairyGUI-Editor.exe" --publish --input {sourceDir} --output {outputDir}'
& 'C:\Program Files\Unity\Hub\Editor\2022.3.62f2\Editor\Unity.exe' `
  -projectPath 'D:\Project\FguiCli' `
  -batchmode `
  -quit `
  -executeMethod Editor.FguiPackagePublisher.PublishFromCommandLine `
  --packageDir 'D:\ProjectGit\AirLegion\fgui_airLegion\assets\BattleUI' `
  --outputDir 'D:\Project\FguiCli\Build\Published\BattleUI' `
  --mode official
```

## 当前约束
- 主要覆盖当前样例里的 `component` / `image` / `atlas`
- 对 `alone_npot`，会分配到新的独立图集索引
- 目前未尝试复原 BattleUI 示例里看起来来自外部分支配置的 `eng` 派生资源生成逻辑；如果源 `package.xml` 本身带有 `branch/branches` 属性，会原样保留

