/* Experimental SDL3_mixer bridge. Control calls are serialized by the caller.
 * Create/destroy on the SDL main thread. No callbacks into managed code. */
#include <SDL3/SDL.h>
#include <SDL3_mixer/SDL_mixer.h>
#include <stdint.h>

#ifdef _WIN32
#define API __declspec(dllexport)
#else
#define API __attribute__((visibility("default")))
#endif

#define MAX_VOICES 128

typedef struct {
    MIX_Audio *audio;
    MIX_Track *track;
    int rate;
} Voice;

typedef struct {
    MIX_Mixer *mixer;
    Voice voices[MAX_VOICES];
    SDL_AtomicInt callbacks;
} Engine;

static void SDLCALL postmix(void *userdata, MIX_Mixer *mixer,
                           const SDL_AudioSpec *spec, float *pcm, int samples)
{
    /* Native-only telemetry. No logging, allocations, locks or CLR callbacks. */
    Engine *engine = userdata;
    SDL_AddAtomicInt(&engine->callbacks, 1);
    (void)mixer; (void)spec; (void)pcm; (void)samples;
}

API const char *g0_audio_error(void) { return SDL_GetError(); }

API Engine *g0_audio_create(int sample_frames, int wasapi)
{
    if (sample_frames < 64 || sample_frames > 4096) {
        SDL_SetError("sample_frames must be between 64 and 4096");
        return NULL;
    }
    /* Must precede first audio initialization. These are hints, not guarantees.
     * Do not change process priority, affinity, or global timer resolution.
     * SDL WASAPI registers its native device thread with MMCSS Pro Audio. */
    if (!SDL_WasInit(SDL_INIT_AUDIO)) {
        char value[16];
        SDL_snprintf(value, sizeof(value), "%d", sample_frames);
        SDL_SetHint(SDL_HINT_AUDIO_DEVICE_SAMPLE_FRAMES, value);
        if (wasapi) SDL_SetHint(SDL_HINT_AUDIO_DRIVER, "wasapi");
    }
    if (!SDL_InitSubSystem(SDL_INIT_AUDIO)) return NULL;
    if (!MIX_Init()) {
        SDL_QuitSubSystem(SDL_INIT_AUDIO);
        return NULL;
    }
    Engine *engine = SDL_calloc(1, sizeof(*engine));
    if (!engine) goto fail;
    SDL_AudioSpec spec = { SDL_AUDIO_F32, 2, 48000 };
    engine->mixer = MIX_CreateMixerDevice(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &spec);
    if (!engine->mixer) goto fail;
    if (!MIX_SetPostMixCallback(engine->mixer, postmix, engine)) {
        MIX_DestroyMixer(engine->mixer);
        goto fail;
    }
    return engine;
fail:
    SDL_free(engine);
    MIX_Quit();
    SDL_QuitSubSystem(SDL_INIT_AUDIO);
    return NULL;
}

static Voice *voice(Engine *engine, int id)
{
    if (!engine || id < 0 || id >= MAX_VOICES || !engine->voices[id].track) {
        SDL_SetError("invalid voice");
        return NULL;
    }
    return &engine->voices[id];
}

API int g0_audio_load(Engine *engine, const char *path)
{
    if (!engine || !path) return -1;
    int id;
    for (id = 0; id < MAX_VOICES; ++id)
        if (!engine->voices[id].track) break;
    if (id == MAX_VOICES) { SDL_SetError("voice capacity exhausted"); return -1; }
    /* Fully decode outside the audio thread. Memory cost scales with duration.
     * This avoids file I/O/codec work during mixing, not all SDL allocations. */
    MIX_Audio *audio = MIX_LoadAudio(engine->mixer, path, true);
    if (!audio) return -1;
    MIX_Track *track = MIX_CreateTrack(engine->mixer);
    SDL_AudioSpec spec;
    if (!track || !MIX_GetAudioFormat(audio, &spec) || !MIX_SetTrackAudio(track, audio)) {
        if (track) MIX_DestroyTrack(track);
        MIX_DestroyAudio(audio);
        return -1;
    }
    engine->voices[id] = (Voice){ audio, track, spec.freq };
    return id;
}

API int g0_audio_play(Engine *engine, int id) {
    Voice *v = voice(engine, id);
    return v && MIX_PlayTrack(v->track, 0);
}
API int g0_audio_stop(Engine *engine, int id) {
    Voice *v = voice(engine, id);
    return v && MIX_StopTrack(v->track, 0);
}
API int g0_audio_gain(Engine *engine, int id, float gain) {
    Voice *v = voice(engine, id);
    return v && MIX_SetTrackGain(v->track, gain);
}
API int g0_audio_frequency(Engine *engine, int id, float ratio) {
    Voice *v = voice(engine, id);
    return v && MIX_SetTrackFrequencyRatio(v->track, ratio);
}
API double g0_audio_position(Engine *engine, int id) {
    Voice *v = voice(engine, id);
    return v ? (double)MIX_GetTrackPlaybackPosition(v->track) / v->rate : -1;
}
API int g0_audio_seek(Engine *engine, int id, double seconds) {
    Voice *v = voice(engine, id);
    if (!v || !(seconds >= 0) || seconds > (double)INT64_MAX / v->rate) return 0;
    return MIX_SetTrackPlaybackPosition(v->track, (Sint64)(seconds * v->rate));
}
API int g0_audio_callbacks(Engine *engine) {
    return engine ? SDL_GetAtomicInt(&engine->callbacks) : 0;
}
API int g0_audio_device_frames(Engine *engine) {
    if (!engine) return -1;
    SDL_PropertiesID props = MIX_GetMixerProperties(engine->mixer);
    SDL_AudioDeviceID device = (SDL_AudioDeviceID)SDL_GetNumberProperty(props, MIX_PROP_MIXER_DEVICE_NUMBER, 0);
    SDL_AudioSpec spec;
    int frames;
    return SDL_GetAudioDeviceFormat(device, &spec, &frames) ? frames : -1;
}
API const char *g0_audio_driver(void) { return SDL_GetCurrentAudioDriver(); }
API void g0_audio_destroy(Engine *engine) {
    if (!engine) return;
    /* Destruction synchronizes with the mix thread before userdata is freed. */
    MIX_SetPostMixCallback(engine->mixer, NULL, NULL);
    for (int i = 0; i < MAX_VOICES; ++i) {
        if (engine->voices[i].track) MIX_DestroyTrack(engine->voices[i].track);
        if (engine->voices[i].audio) MIX_DestroyAudio(engine->voices[i].audio);
    }
    MIX_DestroyMixer(engine->mixer);
    SDL_free(engine);
    MIX_Quit();
    SDL_QuitSubSystem(SDL_INIT_AUDIO);
}
