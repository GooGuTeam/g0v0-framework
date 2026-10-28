// This file is automatically included in every shader.

#ifndef INTERNAL_GLOBAL_UNIFORMS_H
#define INTERNAL_GLOBAL_UNIFORMS_H

#extension GL_ARB_shader_storage_buffer_object : enable

layout(std140, set = -1, binding = 0) uniform g_GlobalUniforms
{
    // Whether the backbuffer is currently being drawn to.
    bool g_BackbufferDraw;

    // Whether the depth values range from 0 to 1. If false, depth values range from -1 to 1.
    // OpenGL uses [-1, 1], Vulkan/D3D/MTL all use [0, 1].
    bool g_IsDepthRangeZeroToOne;

    // Whether the clip space ranges from -1 (top) to 1 (bottom). If false, the clip space ranges from -1 (bottom) to 1 (top).
    bool g_IsClipSpaceYInverted;

    // Whether the texture coordinates begin in the top-left of the texture. If false, (0, 0) is the bottom-left texel of the texture.
    bool g_IsUvOriginTopLeft;

    mat4 g_ProjMatrix;
    mat3 g_ToMaskingSpace;

    bool g_IsMasking;
    highp float g_CornerRadius;
    highp float g_CornerExponent;
    highp vec4 g_MaskingRect;
    highp float g_BorderThickness;
    lowp mat4 g_BorderColour;
    mediump float g_MaskingBlendRange;
    lowp float g_AlphaExponent;
    highp vec2 g_EdgeOffset;
    bool g_DiscardInner;
    highp float g_InnerCornerRadius;

    // 0 -> None
    // 1 -> ClampToEdge
    // 2 -> ClampToBorder
    // 3 -> Repeat
    int g_WrapModeS;
    int g_WrapModeT;

    // The masking buffer entry for vertices that do not carry their own (see sh_Masking.h).
    int g_MaskingIndex;

    // Bit i is set when the texture in slot i has a single (coverage) channel. See sh_Texture.fs.
    int g_SingleChannelSlots;

    // Bit i is set when the texture in slot i uses nearest filtering. Only bindless texture tables read it.
    int g_NearestSlots;
};

// Masking parameters for per-vertex masking (MaskingData.cs). Declared in every shader so that resource set numbering
// is the same in all stages, whether or not a stage uses it.
struct g0v0_MaskingData
{
    highp mat3 ToMaskingSpace;
    highp vec4 MaskingRect;

    // The scissor rectangle in the same space as vertex positions.
    highp vec4 ScissorRect;

    lowp mat4 BorderColour;
    highp float CornerRadius;
    highp float CornerExponent;
    highp float BorderThickness;
    mediump float MaskingBlendRange;
    lowp float AlphaExponent;
    highp float InnerCornerRadius;
    highp vec2 EdgeOffset;
    bool IsMasking;
    bool DiscardInner;
};

#ifndef OSU_GRAPHICS_NO_SSBO

layout(std140, set = -2, binding = 0) readonly buffer g_MaskingBuffer
{
    g0v0_MaskingData Data[];
} g0v0_Masking;

#else // OSU_GRAPHICS_NO_SSBO

// 80 entries keep the block under the 16KiB guaranteed for uniform blocks.
layout(std140, set = -2, binding = 0) uniform g_MaskingBuffer
{
    g0v0_MaskingData Data[80];
} g0v0_Masking;

#endif // OSU_GRAPHICS_NO_SSBO

#endif