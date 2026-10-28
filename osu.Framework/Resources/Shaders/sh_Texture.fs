#ifndef TEXTURE_FS
#define TEXTURE_FS

#include "sh_Utils.h"
#include "sh_Masking.h"
#include "sh_TextureWrapping.h"

layout(location = 2) in mediump vec2 v_TexCoord;

#ifdef G0V0_BINDLESS_TEXTURES
// Vulkan with descriptor indexing (see VeldridBindlessTextures.cs): one table of textures, indexed per primitive. Textures share samplers;
// their views limit them to their valid mip levels, and g_NearestSlots picks the filter.
layout(location = 7) in highp float v_TextureSlot;

layout(set = 0, binding = 0) uniform lowp texture2D m_Textures[16];
layout(set = 0, binding = 1) uniform lowp sampler m_LinearSampler;
layout(set = 0, binding = 2) uniform lowp sampler m_NearestSampler;
#else
layout(set = 0, binding = 0) uniform lowp texture2D m_Texture;
layout(set = 0, binding = 1) uniform lowp sampler m_Sampler;
#endif

// Defined when the vertex shader outputs v_TextureSlot (see TextureSlots.cs). Other vertex shaders may use sets 1 to 3 themselves.
#ifdef G0V0_TEXTURE_SLOTS
// The texture unit this primitive samples (see Renderer.TEXTURE_SLOTS). Constant per primitive.
layout(location = 7) in highp float v_TextureSlot;

// GLES 3.0 only allows constant indices into sampler arrays, so each slot is its own binding.
layout(set = 1, binding = 0) uniform lowp texture2D m_Texture1;
layout(set = 1, binding = 1) uniform lowp sampler m_Sampler1;
layout(set = 2, binding = 0) uniform lowp texture2D m_Texture2;
layout(set = 2, binding = 1) uniform lowp sampler m_Sampler2;
layout(set = 3, binding = 0) uniform lowp texture2D m_Texture3;
layout(set = 3, binding = 1) uniform lowp sampler m_Sampler3;
#endif

layout(location = 0) out vec4 o_Colour;

vec4 sampleTextureSlot(int slot, vec2 wrappedCoord)
{
#ifdef G0V0_BINDLESS_TEXTURES
    // As wrappedSampler(), inlined so that the index keeps its non-uniform decoration.
    if (g_WrapModeS == 2 && (wrappedCoord.x < v_TexRect[0] || wrappedCoord.x > v_TexRect[2]) ||
        g_WrapModeT == 2 && (wrappedCoord.y < v_TexRect[1] || wrappedCoord.y > v_TexRect[3]))
        return vec4(0.0);

    if (((g_NearestSlots >> slot) & 1) != 0)
        return texture(sampler2D(m_Textures[nonuniformEXT(slot)], m_NearestSampler), wrappedCoord, -0.9);

    return texture(sampler2D(m_Textures[nonuniformEXT(slot)], m_LinearSampler), wrappedCoord, -0.9);
#else
#ifdef G0V0_TEXTURE_SLOTS
    if (slot == 1)
        return wrappedSampler(wrappedCoord, v_TexRect, m_Texture1, m_Sampler1, -0.9);
    if (slot == 2)
        return wrappedSampler(wrappedCoord, v_TexRect, m_Texture2, m_Sampler2, -0.9);
    if (slot == 3)
        return wrappedSampler(wrappedCoord, v_TexRect, m_Texture3, m_Sampler3, -0.9);
#endif

    return wrappedSampler(wrappedCoord, v_TexRect, m_Texture, m_Sampler, -0.9);
#endif
}

void main(void)
{
    // Untextured quads (e.g. boxes drawn with the white pixel) mark their texture rectangle as (-2, -2) to (-1, -1).
    // The white pixel samples as opaque white, so skip wrapping and sampling. Constant per primitive, so this does not diverge.
    if (v_TexRect.x < -1.5)
    {
        o_Colour = getRoundedColor(vec4(1.0), v_TexCoord);
        return;
    }

#if defined(G0V0_TEXTURE_SLOTS) || defined(G0V0_BINDLESS_TEXTURES)
    int slot = int(v_TextureSlot + 0.5);
#else
    int slot = 0;
#endif
    vec2 wrappedCoord = wrap(v_TexCoord, v_TexRect);
    vec4 texel = sampleTextureSlot(slot, wrappedCoord);

    // Single-channel font atlases store coverage only. Reading them here rather than in a separate shader lets text batch with sprites.
    if ((g_SingleChannelSlots & (1 << slot)) != 0)
        texel = vec4(1.0, 1.0, 1.0, texel.r);

    o_Colour = getRoundedColor(texel, wrappedCoord);
}

#endif
