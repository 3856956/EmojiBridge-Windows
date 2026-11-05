# 表情搬家 · 微信 ↔ QQ

适用于 Windows 的桌面工具。支持从电脑版微信 4.x 和 QQNT 提取收藏表情，看图挑选后导出为图片，再在目标应用中添加。也可以导入已有的图片或 GIF。

## 启动

在 [GitHub Releases](https://github.com/3856956/EmojiBridge-Windows/releases/latest) 下载 `EmojiBridge-Windows.zip`，解压后双击 **EmojiBridge.exe**。保留同目录的 `third_party` 文件夹，其中是表情提取组件。需要 64 位 Windows 10 / 11 和系统自带的 .NET Framework 4.x。无需额外安装 Python、Node 或微信插件。

## 微信 → QQ

1. 登录电脑版微信，打开聊天窗口里的「我添加的表情」面板，保持微信运行。
2. 在程序中点「从微信提取」。检查识别出的程序、数据目录和账号，选择保存位置，点「开始提取」。如果没有检测到数据目录，可手动选择微信的 `xwechat_files` 文件夹或其中的账号文件夹。
3. 提取完成后点「导入主窗口」，在缩略图窗口挑选想迁移的表情。可以点击图片勾选，也可以全选、全不选或反选。点「应用选择」后，主窗口只会导出勾选的表情。之后也可以通过「看图挑选」重新选择。提取的原始图片可以在「打开图片目录」中查看。
4. 在 QQ 的聊天窗口中打开表情面板，寻找自定义 / 收藏表情的本地添加入口，选择导出的图片。如果 QQ 版本支持多选，可以批量选择。若找不到入口，可手动发送图片，再右键查看是否有添加到表情的选项。QQ 各版本入口、大小及数量限制可能不同。

## QQ → 微信

1. 在 QQ 收藏表情面板向下滚动，让准备迁移的表情加载到电脑。
2. 点「从 QQ 提取」，选择一个账号，再点「导入并挑选」。如果 QQ 数据目录经过迁移，也可手动选择账号目录或其中的 Ori 文件夹。
3. 勾选要迁移的表情，应用选择。导出目标会选为「导出给微信」。点击「导出勾选表情」，生成图片文件夹和可选 ZIP。
4. 在微信表情面板的「我添加的表情」中使用「+」添加导出的图片。若电脑版没有该入口，可以把图片保存到手机相册，再用手机微信添加。

QQ 方向读取本地收藏原图缓存。未加载的表情可能缺失，旧缓存也可能保留已经取消收藏的表情，可在挑选窗口筛掉。支持 QQNT 的 Ori 文件夹和旧版 CustomFace 图片目录。导出到两个应用都需要在目标应用中手动添加。

也可以点击「添加图片」「添加文件夹」，或把图片 / 文件夹拖入主窗口。文件夹会扫描子目录，相同内容的文件会自动去重。

## 功能与限制

- 支持 PNG、JPG、GIF、BMP、TIFF、ICO；按文件内容识别 GIF，纠正错误的 GIF 扩展名。
- 静态图默认输出 PNG，最长边 512 像素，保留透明背景。也可选 256、128 或原始尺寸。仅缩小，不放大。
- JPG 输出会将透明区域填成白色。TIFF / ICO 等静态多页或多尺寸文件只使用默认图像。
- GIF 和含动画控制块的 PNG 按原文件导出。超出目标应用限制的动画需要另行处理。
- 微信方向依赖微信版本的表情库结构。已在本机微信 4.1.15.56 实测。图片链接过期、未同步到电脑或网络不可达时，部分表情可能无法恢复。
- 微信表情库及其对应的运行时解密信息在本机读取，提取组件会请求表情库中记录的图片地址。临时解密信息、数据库副本和链接缓存随提取会话清理。QQ 方向从本地缓存读取图片。
- 整理功能暂不转换 WebP、视频或微信加密缓存。提取到的 WebP 文件仍保留在原始提取目录。单文件限制 64 MB，单图限制 5000 万像素。
- 每次导出创建新目录，附带离线预览 `index.html`、清单 `manifest.json` 和使用说明，可选 ZIP 打包。
- 导出清单含本机源文件路径。分享整个目录 / ZIP 前可删除清单。
- 原图不会被修改。文件损坏、源文件改变或无法读取时会跳过并记录；导出错误记录位于 `errors.txt`。

## 从源码构建

在项目目录打开 PowerShell：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

`src/EmojiBridge.cs` 为主程序，`src/WeChatExtractor.cs` 为提取集成，`src/SelfTest.cs` 为自动验证。编译不需要下载依赖；运行微信提取需要同目录的已校验 `third_party\wxemoticon\wxemoticon.exe`。

提取组件使用 [wxemoticon v0.3.0](https://github.com/liusheng22/export-wechat-emoji/releases/tag/v0.3.0) 的 Windows 发布文件。组件源码声明 MIT 许可，详见 `THIRD-PARTY-NOTICES.txt`。组件缺失时可运行 `node fetch-extractor.mjs` 下载校验，然后解压 `third_party\wxemoticon-v0.3.0.zip` 到 `third_party\wxemoticon`。生成便携包：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\package.ps1`。

运行自动验证：

```powershell
$process = Start-Process -FilePath .\EmojiBridge.exe -ArgumentList '--self-test', '.\test-output' -WindowStyle Hidden -Wait -PassThru
$process.ExitCode
Get-Content .\test-output\test-results.json
```

自动验证包括静态图缩放、透明度、GIF 帧数和逐字节保留、ZIP 内容、重复识别、损坏文件、文件名、源文件变化以及不覆盖已有导出。
