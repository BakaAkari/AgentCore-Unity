# Beautify 3 (URP) 完整使用文档

## 1. 什么是 Beautify？

Beautify 是专为 Unity URP 设计的后处理图像增强插件，旨在通过一系列高质量的视觉效果提升游戏画面表现力。

### 关键特性
- **图像增强**：锐化、边缘抗锯齿、抖动等
- **色调映射与调色**：白平衡、LUT、色彩饱和度等
- **镜头与光照效果**：泛光、变形镜头光晕、太阳光晕、色差、景深等
- **艺术效果**：暗角、描边、夜视、热成像、胶片颗粒等

### 系统要求
- Unity 2022.3 LTS 或更高版本
- 已安装 URP (Universal Render Pipeline)

## 2. 快速入门

1. **导入插件**：通过 Package Manager 或 Asset Store 导入 Beautify
2. **添加 Renderer Feature**：选择 URP Renderer Data asset → Add Renderer Feature > Beautify
3. **创建 Global Volume**：添加 Override → Kronnect > Beautify
4. **启用效果**：设置参数如 Sharpen 4-8, Saturate 0.3, Vignetting outer ring 0.4
5. **验证效果**：进入 Play Mode 查看效果
6. **查看示例**：打开 `Beautify/URP/Demo/` 下的示例场景

## 3. 设置指南

### 前提条件
- Unity 2022.3 LTS+
- 已安装 URP
- 已分配 URP Settings asset

### 设置步骤
1. **添加 Beautify Renderer Feature**：必须添加到每个 URP Renderer Data asset
2. **设置 Render Pass Event**：建议设为 "Before Rendering Transparents" 以忽略透明物体
3. **启用 Post Processing**：在相机上启用或使用 "Ignore Post Processing Option" 以获得更好性能
4. **创建 Global Volume**：添加 Override → Kronnect > Beautify
5. **验证效果**：在 Play Mode 中验证

### 构建设置
- 在 Build Options 中禁用未使用的效果以减少构建大小
- 使用 "Autoselect Unused Beautify Features" 按钮自动剥离未使用的功能

## 4. 示例场景

### 包含的示例场景
- `Beautify/URP/Demo/`：彩色胶囊，多个效果已启用
- `Scene_DepthOfField`：带实时景深的动画球体
- `Scene_LUTBlending`：运行时平滑混合两个 LUT 纹理

### 如何使用
1. 打开场景
2. 进入 Play Mode
3. 选择 PostFX GameObject
4. 展开 Volume 组件
5. 使用 "Enable Compare Mode" 进行前后对比

## 5. 教程

### 视频教程
1. URP Setup
2. Comparison Demo Showcase
3. Depth of Field Transparency Support
4. Image Enhancement & Effects

## 6. 性能优化

### 优化清单
| 优化项 | 影响 | 说明 |
|--------|------|------|
| Bypass Unity Post-Processing | 高 | 禁用相机 Post Processing，启用 "Ignore Post Processing Option"，节省一次全屏 blit |
| Prioritize Shader Performance | 中 | 使用更快的 shader 变体，质量略低 |
| Reduce MSAA | 中 | 从 x4 降到 x2 或禁用 |
| Ignore Depth Texture | 中 | 2D 游戏或不用深度效果时启用 |
| Downscale | 高 | 增加 Downscale 值，移动端效果显著 |
| Fewer effects | 可变 | 每个效果都有 GPU 开销 |
| Strip unused features | 构建大小 | 在 Renderer Feature 中禁用不用的功能 |

### 移动端规则
- 低端移动设备最多使用 2-3 个效果（如 Sharpen + Bloom + Vignetting）

### 性能分析
- 使用 Window > Analysis > Frame Debugger

## 7. 构建技巧

### 减少构建大小
- 在 Beautify Renderer Feature 中展开 Build Options
- 使用 "Autoselect Unused Beautify Features" 按钮自动剥离未使用的功能
- 在 Strip Unity Post Processing Stripping section 移除大量 Unity 后处理 shader 变体

### 手动设置 shader 关键字
```csharp
// 原来的 multi_compile:
#pragma multi_compile_local __ BEAUTIFY_TONEMAP_ACES
// 如果使用 ACES，替换为:
#define BEAUTIFY_TONEMAP_ACES 1
// 如果不使用 ACES，直接删除该行
```

- 所有关键字都是 "local keyword"，不计入 256 个全局关键字上限
- ⚠️ 修改 BeautifyCore.shader 后更新插件会丢失，需重新应用

## 8. 故障排除

### 效果不可见
- 检查 Renderer Feature 是否已添加
- 确认 Volume 存在且 Mode=Global
- 确认相机有 Post Processing 勾选
- 确认 Volume layer mask 包含 volume 的 layer

### 性能低于预期
- 参考 Performance Tips 页面
- 使用 Frame Debugger
- 移动端降低 Bloom Downsampling 和 DoF 质量

### 视觉伪影或闪烁
- 启用 HDR（Quality > HDR）
- 确保 Depth Texture 已启用（用于 DoF/Outline 等）

### 与其他后处理冲突
- 检查 Beautify Renderer Feature 的 render order

### 构建错误或 shader 编译
- 检查 Edit > Project Settings > Graphics > Shader Stripping
- 或将 Beautify shaders 加入 Always Included Shaders

### 场景视图中效果不显示
- Scene view toolbar → Effects dropdown (相机图标) → 启用 Post Processing

## 9. 参数说明

### 通用设置
- **Performance**：性能相关设置

### 图像增强
- **Sharpen**：锐化
- **Edge Antialiasing**：边缘抗锯齿
- **Dither**：抖动

### 色调映射与调色
- **White Balance**：白平衡
- **LUT**：查找表

### 镜头与光照效果
- **Bloom**：泛光
- **Anamorphic Flares**：变形镜头光晕
- **Sun Flares**：太阳光晕
- **Lens Dirt**：镜头污迹
- **Chromatic Aberration**：色差
- **Depth of Field**：景深
- **Eye Adaptation**：眼睛适应
- **Purkinje Shift**：浦肯野效应

### 艺术效果
- **Vignette**：暗角
- **Outline**：描边
- **Night Vision**：夜视
- **Thermal Vision**：热成像
- **Frame**：边框
- **Film Grain & Artifacts**：胶片颗粒
- **Creative Blur**：创意模糊
- **Pixelate**：像素化

### 着色器自定义
- 着色器自定义相关设置

## 10. 脚本编程 API

### BeautifySettings 单例
```csharp
using Beautify.Universal;
// 单 Volume 场景
BeautifySettings.sharpenIntensity.Override(5f);

// 多 Volume 场景
using Beautify.Universal;
volume.profile.TryGet<Beautify>(out var beautify);
beautify.sharpenIntensity.Override(5f);
```

### 通用参数
- `BoolParameter directWrite` - 直接写入相机目标，跳过中间 blit，默认 false
- `BoolParameter downsampling` - 降采样，默认 false
- `BoolParameter downsamplingBilinear` - 双线性降采样，默认 false
- `BeautifyDownsamplingModeParameter downsamplingMode` - 默认 BeautifyEffectsOnly
- `ClampedFloatParameter downsamplingMultiplier` - 范围 1-64，默认 1
- `BoolParameter ignoreDepthTexture` - 不请求深度纹理，默认 false
- `BoolParameter turboMode` - 性能优先模式，默认 false
- `BoolParameter compareMode` - 对比模式，默认 false
- `BoolParameter disabled` - 禁用所有效果，默认 false
- `BoolParameter hideInSceneView` - 在 Scene 视图隐藏，默认 false

### 锐化参数
- `ClampedFloatParameter sharpenIntensity` - 范围 0-25，默认 0
- `ClampedFloatParameter sharpenClamp` - 范围 0-1，默认 0.45
- `ClampedFloatParameter sharpenDepthThreshold` - 范围 0-0.05，默认 0.035
- `ClampedFloatParameter sharpenRelaxation` - 范围 0-0.2，默认 0.08
- `ClampedFloatParameter sharpenMotionSensibility` - 范围 0-1，默认 0.5

### 色彩调校参数
- `ClampedFloatParameter brightness` - 范围 0-2，默认 1.0
- `ClampedFloatParameter contrast` - 范围 0.5-1.5，默认 1.0
- `ClampedFloatParameter saturate` - 范围 -2 到 3，默认 0
- `ClampedFloatParameter sepia` - 范围 0-1，默认 0
- `ColorParameter tintColor` - 默认 Color(1,1,1,0)
- `BeautifyTonemapOperatorParameter tonemap` - 默认 Linear
- `ClampedFloatParameter colorTemp` - 范围 1000-40000，默认 6550
- `ClampedFloatParameter colorTempBlend` - 范围 0-1，默认 0
- `BoolParameter lut` - 默认 false
- `ClampedFloatParameter lutIntensity` - 范围 0-1，默认 0
- `TextureParameter lutTexture` - 默认 null

### 泛光参数
- `FloatParameter bloomIntensity` - 默认 0
- `FloatParameter bloomThreshold` - 默认 0.75
- `ClampedFloatParameter bloomSpread` - 范围 0-1，默认 0.5
- `ClampedIntParameter bloomResolution` - 范围 1-10，默认 1
- `BoolParameter bloomAntiflicker` - 默认 false
- `BoolParameter bloomCustomize` - 默认 false
- bloomBoost0-5, bloomWeight0-5, bloomTint0-5 (6 层自定义)
- `FloatParameter bloomDepthAtten` - 深度衰减，默认 0

### 景深参数
- `BoolParameter depthOfField` - 默认 false
- `BeautifyDoFFocusModeParameter depthOfFieldFocusMode` - 默认 FixedDistance
- `FloatParameter depthOfFieldDistance` - 默认 10
- `FloatParameter depthOfFieldAperture` - 默认 2.8
- `ClampedFloatParameter depthOfFieldFocalLength` - 范围 0.005-0.5，默认 0.050
- `ClampedIntParameter depthOfFieldDownsampling` - 范围 1-5，默认 2
- `ClampedIntParameter depthOfFieldMaxSamples` - 范围 2-16，默认 6
- `BoolParameter depthOfFieldBokeh` - 默认 true
- `BoolParameter depthOfFieldTransparentSupport` - 默认 false
- `BoolParameter depthOfFieldUsePhysicalCamera` - 默认 false

### 眼睛适应参数
- `BoolParameter eyeAdaptation` - 默认 false
- `ClampedFloatParameter eyeAdaptationMinExposure` - 范围 0-1，默认 0.2
- `ClampedFloatParameter eyeAdaptationMaxExposure` - 范围 1-100，默认 5
- `ClampedFloatParameter eyeAdaptationMiddleGray` - 范围 0.001-0.5，默认 0.18
- `FloatParameter eyeAdaptationSpeedToDark` - 默认 0.2
- `FloatParameter eyeAdaptationSpeedToLight` - 默认 0.4

### 暗角参数
- `ClampedFloatParameter vignettingBlink` - 范围 0-1，默认 0
- `ClampedFloatParameter vignettingAspectRatio` - 范围 0-1，默认 1

### 太阳光晕参数
- 大量参数（ghosts 1-4, corona rays 1-2, halo 等）

### 变形镜头光晕参数
- intensity, threshold, spread, tint 等

### 色差参数
- `FloatParameter chromaticAberrationIntensity` - 范围 0-0.1，默认 0

### 浦肯野效应参数
- `BoolParameter purkinje` - 默认 false
- `ClampedFloatParameter purkinjeAmount` - 范围 0-5，默认 1

## 11. 常见问题

### 是否兼容 Unity 6？
是的，所有 Kronnect 资产都完全兼容 Unity 6。最低支持版本是 Unity 2022.3 LTS，包括 Unity 6 和任何更新版本。

### 如何在运行时更改亮度、对比度等属性？
查看第一个示例场景中的 Demo.cs 脚本获取示例代码。例如：
```csharp
BeautifySettings.settings.brightness.Override(0.5f);
```
也请参阅脚本（C#）部分以获取更多关于如何使用脚本控制 Beautify（URP）的详细信息。

### 如何排除 UI 或 2D 元素不受 Beautify 效果影响？
可以使用 Bloom 和 Anamorphic Flares 的排除层选项来排除不透明对象。要排除 2D 或 UI 元素，可以考虑以下可能性：
- 使用设置为 Overlay Mode 的 Canvas
- 在 URP 中将 Render Event 设置为 Before Transparent
- 使用第二个相机渲染 2D/UI 元素（注意 Direct Write to Camera 选项与多相机不兼容）
- 使用 Beautify Render Feature 的相机层遮罩限制 Beautify 效果到特定相机

### 如何在运行时分配 LUT？
查看第一个示例场景中的 Demo.cs 脚本获取示例代码。例如：
```csharp
BeautifySettings.settings.lut.Override(true);
BeautifySettings.settings.lutIntensity.Override(1f);
BeautifySettings.settings.lutTexture.Override(lutTexture);
```
同样，要禁用 LUT 效果，只需停用覆盖状态或覆盖 lut 值为 false：
```csharp
BeautifySettings.settings.lut.Override(false);
```

### Beautify 的效果缺失，主要原因是什么？
为避免此问题，您需要在正确的 Universal Renderer Data 上设置 Beautify Renderer Feature。添加步骤如下：
- 进入 "Edit – Project Settings"
- 选择 "Graphics" 选项卡并双击脚本化渲染管线数据文件
- 再次双击 Default "Renderer Data File"，最后添加 Beautify Renderer Feature

此外，您需要检查 Project Settings – Quality 下的 "Render Pipeline Asset"。那里可以为每个质量级别设置一个 URP 资产，如果分配了特定的 URP 资产，则会使用它而不是 Project Settings / Graphics 中的默认 URP 资产，后者在没有为质量级别分配特定 URP 资产时作为默认值。

### VR 中使用 Beautify 时出现发光边缘
禁用 Beautify URP 渲染器功能中的 "Ignore Postprocessing" 选项。

### 在 2D URP 项目中，2D 灯光在使用 Beautify 时消失
这是因为 Beautify 渲染器功能未添加到渲染列表中。进入 Projects Settings – URP Asset – Universal 2D Render Data 并将 Beautify 添加到渲染列表中。

### 如何通过脚本启用/禁用景深效果？
请使用以下代码示例来适应您的脚本需求：
```csharp
using UnityEngine;
using Beautify.Universal;
namespace Beautify.Demos {
    public class ToggleDoF : MonoBehaviour {
        void Update() {
            if (Input.GetMouseButtonDown(0)) {
                // 通过覆盖体积属性切换 DoF 状态
                bool state = BeautifySettings.settings.depthOfField.value;
                BeautifySettings.settings.depthOfField.Override(!state);
            }
        }
    }
}
```

### 是否可以在场景和 UI 上应用不同的 Beautify 效果？
URP 中的相机堆叠通过将前一个相机生成的输出传递给第二个相机来工作。因此，第二个相机写入前一个相机内容之上。这意味着应用到第二个相机的任何后处理也会应用到第一个相机的内容上。要完全区分相机 1 和 2 的后处理，唯一的方法是将第一个相机渲染到离屏缓冲区（如渲染纹理），并使用自定义渲染功能和着色器将此渲染纹理与第二个相机的结果组合起来。

### 我有一个问题未在指南中涵盖...
请使用 Kronnect 支持并在此处发布您的问题。我们的团队将很快回复您。

### 效果在构建版本和编辑器中看起来不同
这几乎总是由不同质量级别使用的不同 URP 资产引起。在编辑器中您使用一个质量设置，但构建可能选择不同的设置。检查 Project Settings > Quality，确保每个在构建目标中启用的质量级别都分配了相同的 URP 资产，或至少相同的 Beautify Renderer Feature 配置。如果某些效果（锐化、景深、描边、泛光深度衰减）在构建中缺失，请检查 Beautify 体积中的 ignoreDepthTexture 选项。当此选项禁用（默认）时，Beautify 会自动从管道请求深度纹理，所有依赖深度的效果都会工作。如果启用，深度相关效果将被禁用以节省性能。快速重现方法：将编辑器设置为与构建目标相同的质量级别，检查问题是否也出现在那里。

### 为什么在编辑器中撤销滑块更改时，景深目标会重置？
URP 中的 Beautify 配置文件无法存储场景引用，因此 DoF 目标单独存储在 BeautifySettings 组件中，而不是配置文件本身中。当撤销配置文件更改时，目标引用可能会作为副作用重置。作为解决方法，您可以在运行时通过代码设置 DoF 目标，而不是在编辑器中，这可以避免此撤销问题。

### 如何在保持未使用功能剥离的性能优势的同时，为运行时修改启用 Beautify 功能？
不要使用自动剥离选项。相反，展开剥离设置并手动选择您确定不会使用的功能。这允许您保持特定功能（如景深）可用于运行时修改，同时仍剥离其他未使用的功能。

### 如何修复在 URP 中启用 Beautify 时仅出现的水着色器问题？
将 Beautify 渲染事件更改为 "Before Transparents"。

### 如何使用 overrideState 在运行时切换 Beautify 后处理效果？
设置所需效果的 overrideState 属性（例如 chromaticAberrationIntensity.overrideState, vignettingInnerRing.overrideState 等）。如果设置在场景重新加载后不持久，请在修改属性前调用 BeautifySettings.UnloadBeautify() 以确保正确重新初始化。

### 如何通过脚本更改 Beautify URP 中的变形光晕强度？
在修改属性前调用 BeautifySettings.UnloadBeautify()。这会移除任何链接到先前体积的单例 BeautifySettings 快捷方式，确保它将修改场景中的当前配置文件。然后使用 BeautifySettings.settings.anamorphicFlaresIntensity.Override(value) 设置强度。

### 如何优化 Quest 2 上的 Beautify URP 性能？
对于 Quest 2 性能优化：设置 Downscale 为 2，使用 Depth Texture Mode = Force Prepass（最高效），禁用 Clear XR color buffer，启用 Multi-View 而非 Multi-Pass，并参考官方性能提示指南 https://kronnect.com/guides/beautify-urp-performance-tips/。同时检查 Project Settings / Oculus 建议。

### 如何防止在使用多个 Beautify 配置文件和着色器剥离时出现构建错误？
确保在 Project Settings > Quality 中为所有 URP 资产添加 Beautify Render Feature。仅在一个体积中使用剥离选项以避免冲突。如果您想要完全控制，可以直接在代码中注释掉未使用的着色器关键字（每个关键字都是自描述的），或使用 Shader Control 工具从检查器中禁用关键字而不是编辑代码。

### 如何改善轮廓质量并防止调整 Beautify URP 轮廓设置时出现视觉伪影？
增加深度差设置以获得更好的结果。为了获得最佳效果，使用每个对象 ID 技术按对象控制轮廓，而不是进行大的全局调整，这可能会导致视觉不稳定。

### 如何在使用多个 URP 体积时通过代码禁用特定部分的 Beautify？
Beautify 遵循 URP 体积系统。BeautifySettings 快捷方式仅影响主体积。当使用多个体积时，您需要获取每个体积中使用的 VolumeProfile 的引用，并直接在这些配置文件上更新设置。请参阅脚本提示文档中关于其他后处理效果的示例。

### 如何减少游戏启动后构建时 Beautify 使用的大量 RAM？
禁用所有不需要的功能。对于您将始终使用功能，它们可以通过定义替换，如文档中所述（https://kronnect.com/guides/build-tips-beautify-urp/）。这将节省不需要的着色器变体。您可以通过查看构建日志或使用 Shader Control 等工具来检查生成了多少着色器变体。

### 通过定义禁用着色器功能是否会影响运行时 RAM 消耗，除了构建大小和构建时间？
通过定义减少着色器变体将影响构建大小和构建时间。在运行时，Beautify 主要消耗 VRAM（视频内存）来自着色器二进制代码，而不是系统 RAM。内存分析器中显示的 RAM 来自着色器二进制代码，而不是 Beautify 的运行时操作。

### 如何排除特定对象不受 Beautify 的模糊后处理效果影响？
后处理效果本身不支持层遮罩。您有几种替代方案：
1. 将对象渲染到完全不同的相机（尽管这会影响性能）
2. 使用透明渲染队列渲染对象，使其在模糊内容之上而不使其透明
3. 使用单独的高斯模糊着色器与位于对象后面的平面，而不是依赖 Beautify 的后处理
4. 如果您希望在检查对象后面有模糊效果，可以考虑使用景深调整到该对象的距离

### Beautify Render Feature 剥离选项中的复选框是什么意思？
选中 = 排除/剥离该功能。未选中 = 包含该功能。剥离选项是全局的，影响所有场景的编译。

### 为什么即使配置了设置，Beautify 更改在场景中也不起作用？
检查 Project Settings > Quality 中是否有多个 URP 资产配置。在构建期间，可能会选择不同的 URP 资产，具有不同的剥离设置，从而覆盖您的配置。确保所有质量级别 URP 资产具有相同的 Beautify 剥离设置。

### 如何配置 Beautify 的景深，使不同距离的对象以不同强度模糊，而不是均匀模糊？
DoF 模糊从焦点距离点开始逐渐变化。离焦点越近的物体模糊越少，离焦点越远的物体模糊越强烈。调整焦点距离以设置锐利焦点点，然后增加焦距和光圈值以控制模糊梯度。您可能需要将焦距设置为高值以达到所需效果。模糊强度随离焦点距离增加。

### 如何在特定对象上动态实现变形光晕？
使用层遮罩功能指定哪些对象可以产生变形光晕。如果需要动态控制，您可以在运行时通过脚本更改光源所在的层。

### 在 URP 的 Beautify 3 中，如何在运行时切换不同的效果设置，类似于 Beautify 2 中的配置文件切换？
URP 的 Beautify 3 使用基于体积配置文件的 URP 体积框架，而不是自己的配置文件系统。您可以创建具有自定义设置的不同体积配置文件，并将其分配给体积组件。在运行时，您可以交换体积配置文件以更改效果（例如，当相机进入特定区域时切换到水下效果）。内置管道版本的 Beautify 仍使用自己的配置文件，但 URP 使用更通用的体积配置文件概念。

### 为什么即使不需要，Beautify 着色器也会包含在构建中？
Beautify 着色器存储在 Resources 文件夹中，Unity 会自动编译并包含 Resources 文件夹中的任何内容到构建中。为了减少构建大小，您可能需要重新组织着色器位置或使用条件编译。

### Beautify URP 中的层排除遮罩如何与锐度一起工作？
层排除遮罩从指定层中排除锐度，但它按像素操作而不是按层操作。如果未排除层的另一个层渲染在排除层像素之上，这些顶部像素也会失去锐化，因为排除是基于像素位置而不是层优先级。

### URP 资产列表中出现 "renderer is missing or invalid" 错误的原因是什么？
某些渲染器在 URP 资产的列表中缺失或无效。开发者提供了一个修复，如果发生这种情况可以防止错误。作为解决方法，尝试移除并重新添加列表中的所有条目，确保 UniversalRenderPipelineAsset_Renderer 放在第一位。

### 如何为不同对象层应用不同颜色的轮廓，例如默认对象的白色轮廓和敌人的红色轮廓？
Beautify 本身不支持多种轮廓颜色。要实现这一点，可以结合 Beautify 和 Highlight Plus：在透明通道之前应用 Beautify 用于默认层，使用 Highlight Plus 为敌人轮廓应用不同颜色。

### 在 Quest 3 上使用 Vulkan 单通道渲染时，为什么会出现分割和水平翻转的视觉伪影？
当启用注视渲染时会出现此问题。Beautify 尚不支持注视渲染。在您的 Quest 3 VR 设置中禁用注视渲染以解决此伪影。

### 为什么当 Spine 2D 资产未位于其他资产上方时，景深不能正确应用？
在 Beautify 设置中启用透明支持选项。此选项位于配置面板中，允许景深正确处理透明或分层的 2D 资产。

### 如何在没有体积系统的情况下在场景的不同区域实现动态亮度/曝光调整？
Beautify 提供了两种替代方案：
1. 使用眼睛适应，它提供基于场景亮度的自动曝光调整，具有可配置的最小/最大曝光、过渡速度和中心加权
2. 使用带有触发器碰撞器的脚本，当玩家进入不同区域时通过 API 更改 Beautify 设置，动态调整亮度、对比度或曝光值

### 我有一个未在此处涵盖的问题
请访问支持中心并使用我们的 AI 支持助手获取答案。如果问题仍然存在，请提交一个重现项目，以便我们进一步调查并帮助您。