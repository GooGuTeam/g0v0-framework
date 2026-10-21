// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <stdio.h>

int wmain(int argc, wchar_t **argv) {
    if (argc < 2 || argc > 3) return 2;
    bool gameRecording = argc == 3;
    if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) return 3;
    if (FAILED(MFStartup(MF_VERSION))) { CoUninitialize(); return 4; }
    IMFSourceReader *reader = nullptr;
    IMFMediaType *type = nullptr;
    HRESULT hr = MFCreateSourceReaderFromURL(argv[1], nullptr, &reader);
    if (SUCCEEDED(hr)) hr = MFCreateMediaType(&type);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
    if (SUCCEEDED(hr)) hr = reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, nullptr, type);
    unsigned frames = 0;
    LONGLONG last = -1;
    while (SUCCEEDED(hr)) {
        DWORD flags = 0;
        LONGLONG time = 0;
        IMFSample *sample = nullptr;
        hr = reader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, nullptr, &flags, &time, &sample);
        if (sample) {
            if (time <= last) hr = E_FAIL;
            last = time;
            IMFMediaBuffer *buffer = nullptr;
            HRESULT pixelResult = sample->ConvertToContiguousBuffer(&buffer);
            if (SUCCEEDED(pixelResult)) {
                BYTE *bytes = nullptr;
                DWORD length = 0;
                pixelResult = buffer->Lock(&bytes, nullptr, &length);
                if (SUCCEEDED(pixelResult)) {
                    // Test-only CPU decode/readback. Bright green and black must
                    // alternate, including after each capture-pool slot is reused.
                    if (!length || (!gameRecording && ((frames % 2) ? bytes[0] > 35 : bytes[0] < 60))) hr = E_FAIL;
                    buffer->Unlock();
                }
                buffer->Release();
            }
            if (FAILED(pixelResult)) hr = pixelResult;
            frames++;
            sample->Release();
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
        if (frames > 10000) { hr = E_FAIL; break; }
    }
    unsigned audioBytes = 0;
    if (type) { type->Release(); type = nullptr; }
    if (SUCCEEDED(hr)) hr = MFCreateMediaType(&type);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_Float);
    if (SUCCEEDED(hr)) hr = reader->SetCurrentMediaType(MF_SOURCE_READER_FIRST_AUDIO_STREAM, nullptr, type);
    while (SUCCEEDED(hr)) {
        DWORD flags = 0;
        IMFSample *sample = nullptr;
        hr = reader->ReadSample(MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, nullptr, &flags, nullptr, &sample);
        if (sample) {
            DWORD bytes = 0;
            sample->GetTotalLength(&bytes);
            audioBytes += bytes;
            sample->Release();
        }
        if (flags & MF_SOURCE_READERF_ENDOFSTREAM) break;
        if (audioBytes > 10000000) { hr = E_FAIL; break; }
    }
    printf("Decoded float PCM bytes=%u (AAC padding allowed)\n", audioBytes);
    if (gameRecording ? audioBytes == 0 : (audioBytes < 96000 * 8 || audioBytes > 100000 * 8)) hr = E_FAIL;
    if (type) type->Release();
    if (reader) reader->Release();
    MFShutdown(); CoUninitialize();
    printf("Decode result=0x%08lx frames=%u last_timestamp=%lld\n", (unsigned long)hr, frames, (long long)last);
    return FAILED(hr) || (gameRecording ? (frames < 2 || last <= 0) : (frames != 60 || last < 19600000 || last > 19700000));
}
