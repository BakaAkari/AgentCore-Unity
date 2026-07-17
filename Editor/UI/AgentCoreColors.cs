using UnityEngine;

namespace AgentCore.Editor.UI
{
    /// <summary>
    /// AgentCore UI 统一色板 —— C# 侧单一真源。
    /// <para>
    /// 背景：语义色（成功绿 / 危险红 / 主题蓝等）此前散落在 USS 与多个组件的
    /// C# 硬编码里，出现了"同一个成功绿有三个不同值"（#5cb85c / #4CAF50 /
    /// Color(0.2,0.8,0.3)）的不一致问题。本类把 C# 侧的语义色收敛到一处。
    /// </para>
    /// <para>
    /// 与 USS 的关系：Unity 的 USS 自定义变量（--var）无法被 C# 读取，因此无法做到
    /// "一处定义、两边共享"。约定以本类的十六进制值为准，USS 中 <c>ChatWindow.uss</c>
    /// 顶部的 <c>:root</c> 变量块使用相同的十六进制字符串镜像同步（改一处必须同步另一处）。
    /// </para>
    /// <para>
    /// 标准值取自 USS 中出现频率最高的值（成功绿 #5cb85c×18、危险红 #d9534f×10、
    /// 主题蓝 #4a86c8×12），让原本发散的 C# 硬编码向 USS 主流值收敛，视觉最终一致。
    /// </para>
    /// </summary>
    public static class AgentCoreColors
    {
        // ============ 语义色（状态） ============

        /// <summary>成功 / 完成。#5cb85c</summary>
        public static readonly Color Success = Hex(0x5c, 0xb8, 0x5c);

        /// <summary>危险 / 失败 / 错误。#d9534f</summary>
        public static readonly Color Danger = Hex(0xd9, 0x53, 0x4f);

        /// <summary>警告。#f0ad4e</summary>
        public static readonly Color Warning = Hex(0xf0, 0xad, 0x4e);

        /// <summary>橙色中间态（进度条 70%~90% 档）。#ff8000</summary>
        public static readonly Color Orange = Hex(0xff, 0x80, 0x00);

        // ============ 主题色 ============

        /// <summary>主题蓝 / accent / 进行中。#4a86c8</summary>
        public static readonly Color Accent = Hex(0x4a, 0x86, 0xc8);

        // ============ 中性文字色 ============

        /// <summary>主要文字。#d4d4d4</summary>
        public static readonly Color TextPrimary = Hex(0xd4, 0xd4, 0xd4);

        /// <summary>次要文字。#888888</summary>
        public static readonly Color TextSecondary = Hex(0x88, 0x88, 0x88);

        // ============ 卡片 / 面板背景 ============

        /// <summary>卡片背景。#2d2d2d</summary>
        public static readonly Color CardBackground = Hex(0x2d, 0x2d, 0x2d);

        /// <summary>详情 / 深色区背景。#272727</summary>
        public static readonly Color DetailBackground = Hex(0x27, 0x27, 0x27);

        /// <summary>
        /// 从 8-bit RGB 分量构造 Color（sRGB，alpha=1）。
        /// </summary>
        private static Color Hex(int r, int g, int b)
        {
            return new Color(r / 255f, g / 255f, b / 255f, 1f);
        }
    }
}
