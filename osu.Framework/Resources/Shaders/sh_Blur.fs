#ifndef BLUR_FS
#define BLUR_FS

#include "sh_Utils.h"

layout(location = 2) in mediump vec2 v_TexCoord;

layout(std140, set = 0, binding = 0) uniform m_BlurParameters
{
	mediump vec2 g_TexSize;
	int g_Radius;
	mediump float g_Sigma;
	highp vec2 g_BlurDirection;
};

layout(set = 1, binding = 0) uniform lowp texture2D m_Texture;
layout(set = 1, binding = 1) uniform lowp sampler m_Sampler;

layout(location = 0) out vec4 o_Colour;

lowp vec4 blur(int radius, highp vec2 direction, mediump vec2 texCoord, mediump vec2 texSize, mediump float sigma)
{
	// Gaussian weights are evaluated relative to the centre tap: the 1/(sqrt(2pi)*sigma) normalisation cancels in sum / totalFactor.
	// Taps sit at x = i - 0.5 for i = 2, 4, ..., so successive weights exp(-x^2 / (2 sigma^2)) differ by a ratio which itself
	// shrinks by a constant factor each step. This replaces one exp() and two divisions per tap with three multiplications.
	highp float invSigmaSq = 1.0 / (sigma * sigma);
	highp float weight = exp(-1.125 * invSigmaSq); // x = 1.5
	highp float ratio = exp(-5.0 * invSigmaSq);    // weight(x + 2) / weight(x) at x = 1.5
	highp float ratioStep = exp(-4.0 * invSigmaSq);

	highp vec2 texelStep = direction / texSize;

	mediump vec4 sum = texture(sampler2D(m_Texture, m_Sampler), texCoord);
	highp float totalFactor = 1.0;

	for (int i = 2; i <= 200; i += 2)
	{
		highp float factor = weight * 2.0;
		highp vec2 offset = texelStep * (float(i) - 0.5);
		totalFactor += 2.0 * factor;
		sum += texture(sampler2D(m_Texture, m_Sampler), texCoord + offset) * factor;
		sum += texture(sampler2D(m_Texture, m_Sampler), texCoord - offset) * factor;
		if (i >= radius)
			break;

		weight *= ratio;
		ratio *= ratioStep;
	}

	// this is supposed to be unnecessary with https://github.com/ppy/veldrid-spirv/pull/4,
	// but blurring is still broken on some Apple devices when removing it (at least on an M2 iPad Pro and an iPhone 12).
	// todo: investigate this.
	float one = g_BackbufferDraw ? 0 : 1;

	return sum / totalFactor * one;
}

void main(void)
{
	o_Colour = blur(g_Radius, g_BlurDirection, v_TexCoord, g_TexSize, g_Sigma);
}

#endif