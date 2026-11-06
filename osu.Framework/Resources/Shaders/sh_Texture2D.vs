#ifndef TEXTURE2D_VS
#define TEXTURE2D_VS

#include "sh_Utils.h"

// The number of texture slots packed into m_MaskingIndex (Renderer.TextureSlotCount).
#ifndef G0V0_TEXTURE_SLOT_COUNT
#define G0V0_TEXTURE_SLOT_COUNT 4.0
#endif

layout(location = 0) in highp vec2 m_Position;
layout(location = 1) in lowp vec4 m_Colour;
layout(location = 2) in highp vec2 m_TexCoord;
layout(location = 3) in highp vec4 m_TexRect;
layout(location = 4) in mediump vec2 m_BlendRange;
layout(location = 5) in highp float m_BackbufferDrawDepth;

// Packs (one more than the masking buffer entry, or 0 to use g_MaskingIndex) * G0V0_TEXTURE_SLOT_COUNT + the texture slot sampled by sh_Texture.fs.
// Declaring this input opts in to per-vertex masking.
layout(location = 6) in highp float m_MaskingIndex;

layout(location = 0) out highp vec2 v_MaskingPosition;
layout(location = 1) out lowp vec4 v_Colour;
layout(location = 2) out highp vec2 v_TexCoord;
layout(location = 3) out highp vec4 v_TexRect;
layout(location = 4) out mediump vec2 v_BlendRange;
layout(location = 5) out highp float v_MaskingIndex;
layout(location = 6) out highp vec2 v_ScissorPosition;
layout(location = 7) out highp float v_TextureSlot;

void main(void)
{
    // Both parts are small integers, so float arithmetic on them is exact.
    highp float textureSlot = mod(m_MaskingIndex, G0V0_TEXTURE_SLOT_COUNT);
    highp float maskingEntry = (m_MaskingIndex - textureSlot) / G0V0_TEXTURE_SLOT_COUNT;
    v_TextureSlot = textureSlot;

#ifdef G0V0_PER_VERTEX_MASKING
    int maskingIndex = maskingEntry > 0.5 ? int(maskingEntry - 0.5) : g_MaskingIndex;
    highp mat3 toMaskingSpace = g0v0_Masking.Data[maskingIndex].ToMaskingSpace;
    v_MaskingIndex = float(maskingIndex);
#else
    highp mat3 toMaskingSpace = g_ToMaskingSpace;
    v_MaskingIndex = 0.0;
#endif

    v_ScissorPosition = m_Position;

    // Transform from screen space to masking space.
    highp vec3 maskingPos = toMaskingSpace * vec3(m_Position, 1.0);
    v_MaskingPosition = maskingPos.xy / maskingPos.z;

    v_Colour = m_Colour;
    v_TexCoord = m_TexCoord;
    v_TexRect = m_TexRect;
    v_BlendRange = m_BlendRange;

    gl_Position = g_ProjMatrix * vec4(m_Position, 1.0, 1.0);

#ifdef G0V0_DRAW_DEPTH_ALWAYS
    gl_Position.z = m_BackbufferDrawDepth;
#else
    if (g_BackbufferDraw)
        gl_Position.z = m_BackbufferDrawDepth;
#endif
}

#endif