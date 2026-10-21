// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#pragma once
#include <evr.h>
#include <atomic>

// Some MinGW SDK revisions expose IMFTrackedSample but omit this exported function.
extern "C" HRESULT WINAPI MFCreateTrackedSample(IMFTrackedSample **sample);

// Independent of Encoder lifetime. MF owns a COM reference until Invoke completes;
// the consumer owns another until its frame lease has been returned.
class FrameCompletion final : public IMFAsyncCallback {
    std::atomic<ULONG> references{1};
    std::atomic<bool> completed{false};
public:
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void **result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id != IID_IUnknown && id != __uuidof(IMFAsyncCallback)) return E_NOINTERFACE;
        *result = static_cast<IMFAsyncCallback *>(this);
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override {
        ULONG remaining = --references;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE GetParameters(DWORD *, DWORD *) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE Invoke(IMFAsyncResult *) override {
        completed.store(true, std::memory_order_release);
        return S_OK;
    }
    bool IsComplete() const { return completed.load(std::memory_order_acquire); }
};

API int g0_video_frame_complete(FrameCompletion *ticket) { return ticket && ticket->IsComplete(); }
API void g0_video_frame_release(FrameCompletion *ticket) { if (ticket) ticket->Release(); }
