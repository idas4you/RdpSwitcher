#include <windows.h>
#include <tsvirtualchannels.h>

#include <new>

namespace
{
    constexpr char kChannelName[] = "RDPSWCH";
    constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\RdpSwitcher.Signal";

    const CLSID CLSID_RdpSwitcherPlugin =
    {
        0x8A1E8AC0,
        0x827E,
        0x42F8,
        { 0x8B, 0x65, 0x8D, 0x65, 0xC7, 0xA6, 0xAB, 0x7D }
    };

    long g_objectCount = 0;
    long g_lockCount = 0;

    void Trace(const wchar_t* message)
    {
        OutputDebugStringW(L"RdpSwitcher.Plugin: ");
        OutputDebugStringW(message);
        OutputDebugStringW(L"\n");
    }

    bool ForwardToHostPipe(const BYTE* buffer, ULONG size)
    {
        if (buffer == nullptr || size == 0)
        {
            return false;
        }

        HANDLE pipe = CreateFileW(
            kPipeName,
            GENERIC_WRITE,
            0,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);

        if (pipe == INVALID_HANDLE_VALUE && GetLastError() == ERROR_PIPE_BUSY)
        {
            if (WaitNamedPipeW(kPipeName, 500))
            {
                pipe = CreateFileW(
                    kPipeName,
                    GENERIC_WRITE,
                    0,
                    nullptr,
                    OPEN_EXISTING,
                    FILE_ATTRIBUTE_NORMAL,
                    nullptr);
            }
        }

        if (pipe == INVALID_HANDLE_VALUE)
        {
            Trace(L"host named pipe is not available");
            return false;
        }

        DWORD written = 0;
        const BOOL ok = WriteFile(pipe, buffer, size, &written, nullptr);
        FlushFileBuffers(pipe);
        CloseHandle(pipe);

        if (!ok || written != size)
        {
            Trace(L"failed to write DVC payload to host named pipe");
            return false;
        }

        return true;
    }

    class ChannelCallback final : public IWTSVirtualChannelCallback
    {
    public:
        ChannelCallback()
        {
            InterlockedIncrement(&g_objectCount);
        }

        ChannelCallback(const ChannelCallback&) = delete;
        ChannelCallback& operator=(const ChannelCallback&) = delete;

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;
            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IWTSVirtualChannelCallback))
            {
                *object = static_cast<IWTSVirtualChannelCallback*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        ULONG STDMETHODCALLTYPE AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_refCount));
        }

        ULONG STDMETHODCALLTYPE Release() override
        {
            const long count = InterlockedDecrement(&_refCount);
            if (count == 0)
            {
                delete this;
            }

            return static_cast<ULONG>(count);
        }

        HRESULT STDMETHODCALLTYPE OnDataReceived(ULONG cbSize, BYTE* pBuffer) override
        {
            ForwardToHostPipe(pBuffer, cbSize);
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnClose() override
        {
            return S_OK;
        }

    private:
        ~ChannelCallback()
        {
            InterlockedDecrement(&g_objectCount);
        }

        long _refCount = 1;
    };

    class RdpSwitcherPlugin final : public IWTSPlugin, public IWTSListenerCallback
    {
    public:
        RdpSwitcherPlugin()
        {
            InterlockedIncrement(&g_objectCount);
        }

        RdpSwitcherPlugin(const RdpSwitcherPlugin&) = delete;
        RdpSwitcherPlugin& operator=(const RdpSwitcherPlugin&) = delete;

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;
            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IWTSPlugin))
            {
                *object = static_cast<IWTSPlugin*>(this);
            }
            else if (IsEqualIID(riid, IID_IWTSListenerCallback))
            {
                *object = static_cast<IWTSListenerCallback*>(this);
            }
            else
            {
                return E_NOINTERFACE;
            }

            AddRef();
            return S_OK;
        }

        ULONG STDMETHODCALLTYPE AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_refCount));
        }

        ULONG STDMETHODCALLTYPE Release() override
        {
            const long count = InterlockedDecrement(&_refCount);
            if (count == 0)
            {
                delete this;
            }

            return static_cast<ULONG>(count);
        }

        HRESULT STDMETHODCALLTYPE Initialize(IWTSVirtualChannelManager* channelManager) override
        {
            if (channelManager == nullptr)
            {
                return E_POINTER;
            }

            IWTSListener* listener = nullptr;
            const HRESULT hr = channelManager->CreateListener(
                kChannelName,
                0,
                static_cast<IWTSListenerCallback*>(this),
                &listener);

            if (FAILED(hr))
            {
                Trace(L"CreateListener failed");
                return hr;
            }

            if (_listener != nullptr)
            {
                _listener->Release();
            }

            _listener = listener;
            Trace(L"listener created");
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE Connected() override
        {
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE Disconnected(DWORD) override
        {
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE Terminated() override
        {
            ReleaseListener();
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnNewChannelConnection(
            IWTSVirtualChannel*,
            BSTR,
            BOOL* accept,
            IWTSVirtualChannelCallback** callback) override
        {
            if (accept == nullptr || callback == nullptr)
            {
                return E_POINTER;
            }

            auto channelCallback = new (std::nothrow) ChannelCallback();
            if (channelCallback == nullptr)
            {
                *accept = FALSE;
                *callback = nullptr;
                return E_OUTOFMEMORY;
            }

            *accept = TRUE;
            *callback = channelCallback;
            Trace(L"new DVC channel accepted");
            return S_OK;
        }

    private:
        ~RdpSwitcherPlugin()
        {
            ReleaseListener();
            InterlockedDecrement(&g_objectCount);
        }

        void ReleaseListener()
        {
            if (_listener != nullptr)
            {
                _listener->Release();
                _listener = nullptr;
            }
        }

        long _refCount = 1;
        IWTSListener* _listener = nullptr;
    };

    class ClassFactory final : public IClassFactory
    {
    public:
        ClassFactory()
        {
            InterlockedIncrement(&g_objectCount);
        }

        ClassFactory(const ClassFactory&) = delete;
        ClassFactory& operator=(const ClassFactory&) = delete;

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;
            if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
            {
                *object = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }

            return E_NOINTERFACE;
        }

        ULONG STDMETHODCALLTYPE AddRef() override
        {
            return static_cast<ULONG>(InterlockedIncrement(&_refCount));
        }

        ULONG STDMETHODCALLTYPE Release() override
        {
            const long count = InterlockedDecrement(&_refCount);
            if (count == 0)
            {
                delete this;
            }

            return static_cast<ULONG>(count);
        }

        HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID riid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }

            *object = nullptr;
            if (outer != nullptr)
            {
                return CLASS_E_NOAGGREGATION;
            }

            auto plugin = new (std::nothrow) RdpSwitcherPlugin();
            if (plugin == nullptr)
            {
                return E_OUTOFMEMORY;
            }

            const HRESULT hr = plugin->QueryInterface(riid, object);
            plugin->Release();
            return hr;
        }

        HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override
        {
            if (lock)
            {
                InterlockedIncrement(&g_lockCount);
            }
            else
            {
                InterlockedDecrement(&g_lockCount);
            }

            return S_OK;
        }

    private:
        ~ClassFactory()
        {
            InterlockedDecrement(&g_objectCount);
        }

        long _refCount = 1;
    };
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
    }

    return TRUE;
}

STDAPI DllGetClassObject(
    REFCLSID clsid,
    REFIID iid,
    LPVOID* object)
{
    if (object == nullptr)
    {
        return E_POINTER;
    }

    *object = nullptr;
    if (!IsEqualCLSID(clsid, CLSID_RdpSwitcherPlugin))
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto factory = new (std::nothrow) ClassFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }

    const HRESULT hr = factory->QueryInterface(iid, object);
    factory->Release();
    return hr;
}

STDAPI DllCanUnloadNow()
{
    return (g_objectCount == 0 && g_lockCount == 0) ? S_OK : S_FALSE;
}
