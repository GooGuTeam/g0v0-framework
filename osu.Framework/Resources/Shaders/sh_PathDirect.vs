#ifndef PATH_DIRECT_VS
#define PATH_DIRECT_VS

// Paths drawn without an intermediate framebuffer test their vertex depth in any target with a depth attachment, not only the backbuffer.
#define G0V0_DRAW_DEPTH_ALWAYS

#include "sh_Texture2D.vs"

#endif
