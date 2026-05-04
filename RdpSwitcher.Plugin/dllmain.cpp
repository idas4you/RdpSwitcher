#include <windows.h>
#include <tsvirtualchannels.h>

#include <cstdio>
#include <cstring>
#include <cwchar>
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
    CRITICAL_SECTION g_pipeLock;
    HANDLE g_pipe = INVALID_HANDLE_VALUE;

    void WritePluginLog(const wchar_t* message)
    {
        if (message == nullptr || message[0] == L'\0')
        {
            return;
        }

        wchar_t localAppData[MAX_PATH] = {};
        const DWORD localAppDataLength = GetEnvironmentVariableW(
            L"LOCALAPPDATA",
            localAppData,
            ARRAYSIZE(localAppData));
        if (localAppDataLength == 0 || localAppDataLength >= ARRAYSIZE(localAppData))
        {
            return;
        }

        wchar_t logDirectory[MAX_PATH] = {};
        if (swprintf_s(logDirectory, L"%s\\RdpSwitcher", localAppData) < 0)
        {
            return;
        }

        CreateDirectoryW(logDirectory, nullptr);

        SYSTEMTIME now = {};
        GetLocalTime(&now);

        wchar_t logPath[MAX_PATH] = {};
        if (swprintf_s(
            logPath,
            L"%s\\RdpSwitcher.Plugin-%04u-%02u-%02u.log",
            logDirectory,
            now.wYear,
            now.wMonth,
            now.wDay) < 0)
        {
            return;
        }

        const HANDLE file = CreateFileW(
            logPath,
            FILE_APPEND_DATA,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_ALWAYS,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file == INVALID_HANDLE_VALUE)
        {
            return;
        }

        wchar_t line[1024] = {};
        if (swprintf_s(
            line,
            L"%04u-%02u-%02u %02u:%02u:%02u.%03u %s\r\n",
            now.wYear,
            now.wMonth,
            now.wDay,
            now.wHour,
            now.wMinute,
            now.wSecond,
            now.wMilliseconds,
            message) >= 0)
        {
            char utf8Line[4096] = {};
            const int bytes = WideCharToMultiByte(
                CP_UTF8,
                0,
                line,
                -1,
                utf8Line,
                ARRAYSIZE(utf8Line),
                nullptr,
                nullptr);
            if (bytes > 1)
            {
                DWORD written = 0;
                WriteFile(file, utf8Line, static_cast<DWORD>(bytes - 1), &written, nullptr);
            }
        }

        CloseHandle(file);
    }

    void Trace(const wchar_t* message)
    {
        OutputDebugStringW(L"RdpSwitcher.Plugin: ");
        OutputDebugStringW(message);
        OutputDebugStringW(L"\n");
        WritePluginLog(message);
    }

    void TraceWin32(const wchar_t* message, DWORD error)
    {
        wchar_t buffer[256] = {};
        swprintf_s(buffer, L"%s. Win32Error=%lu", message, error);
        Trace(buffer);
    }

    HANDLE OpenHostPipeWithRetry()
    {
        constexpr DWORD kTimeoutMs = 2000;
        constexpr DWORD kRetryDelayMs = 50;

        const DWORD started = GetTickCount();
        DWORD lastError = ERROR_SUCCESS;

        while (GetTickCount() - started <= kTimeoutMs)
        {
            HANDLE pipe = CreateFileW(
                kPipeName,
                GENERIC_WRITE,
                0,
                nullptr,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                nullptr);

            if (pipe != INVALID_HANDLE_VALUE)
            {
                return pipe;
            }

            lastError = GetLastError();
            if (lastError == ERROR_PIPE_BUSY)
            {
                WaitNamedPipeW(kPipeName, kRetryDelayMs);
            }
            else if (lastError != ERROR_FILE_NOT_FOUND && lastError != ERROR_PATH_NOT_FOUND)
            {
                TraceWin32(L"host named pipe open failed", lastError);
                return INVALID_HANDLE_VALUE;
            }

            Sleep(kRetryDelayMs);
        }

        TraceWin32(L"host named pipe is not available", lastError);
        return INVALID_HANDLE_VALUE;
    }

    void CloseHostPipeNoLock()
    {
        if (g_pipe == INVALID_HANDLE_VALUE)
        {
            return;
        }

        CloseHandle(g_pipe);
        g_pipe = INVALID_HANDLE_VALUE;
    }

    bool EnsureHostPipeNoLock()
    {
        if (g_pipe != INVALID_HANDLE_VALUE)
        {
            return true;
        }

        g_pipe = OpenHostPipeWithRetry();
        return g_pipe != INVALID_HANDLE_VALUE;
    }

    bool WriteHostPipeNoLock(const BYTE* buffer, ULONG size)
    {
        if (!EnsureHostPipeNoLock())
        {
            return false;
        }

        if (size > 8192)
        {
            Trace(L"DVC payload is too large for host named pipe frame");
            return false;
        }

        const DWORD payloadSize = static_cast<DWORD>(size);
        DWORD written = 0;
        BOOL ok = WriteFile(g_pipe, &payloadSize, sizeof(payloadSize), &written, nullptr);
        if (!ok || written != sizeof(payloadSize))
        {
            TraceWin32(L"failed to write DVC payload length to host named pipe", GetLastError());
            CloseHostPipeNoLock();
            return false;
        }

        written = 0;
        ok = WriteFile(g_pipe, buffer, size, &written, nullptr);
        if (!ok || written != size)
        {
            TraceWin32(L"failed to write DVC payload to host named pipe", GetLastError());
            CloseHostPipeNoLock();
            return false;
        }

        return true;
    }

    bool ForwardToHostPipe(const BYTE* buffer, ULONG size)
    {
        if (buffer == nullptr || size == 0)
        {
            return false;
        }

        EnterCriticalSection(&g_pipeLock);
        bool forwarded = WriteHostPipeNoLock(buffer, size);
        if (!forwarded)
        {
            forwarded = WriteHostPipeNoLock(buffer, size);
        }

        LeaveCriticalSection(&g_pipeLock);

        if (!forwarded)
        {
            return false;
        }

        Trace(L"DVC payload forwarded to host named pipe");
        return true;
    }

    bool PreconnectHostPipe()
    {
        EnterCriticalSection(&g_pipeLock);
        const bool connected = EnsureHostPipeNoLock();
        LeaveCriticalSection(&g_pipeLock);

        Trace(connected
            ? L"host named pipe connected after DVC channel connection"
            : L"host named pipe preconnect failed after DVC channel connection; will retry when DVC data arrives");

        return connected;
    }

    bool TryExtractNonce(const BYTE* buffer, ULONG size, char* nonce, size_t nonceSize)
    {
        if (buffer == nullptr || nonce == nullptr || nonceSize == 0)
        {
            return false;
        }

        constexpr char kMarker[] = "nonce=";
        constexpr size_t kMarkerLength = sizeof(kMarker) - 1;

        for (ULONG index = 0; index + kMarkerLength <= size; index++)
        {
            if (memcmp(buffer + index, kMarker, kMarkerLength) != 0)
            {
                continue;
            }

            size_t outputIndex = 0;
            index += static_cast<ULONG>(kMarkerLength);
            while (index < size && outputIndex < nonceSize - 1)
            {
                const char value = static_cast<char>(buffer[index]);
                if (value == '\r' || value == '\n' || value == '\0')
                {
                    break;
                }

                nonce[outputIndex++] = value;
                index++;
            }

            nonce[outputIndex] = '\0';
            return outputIndex > 0;
        }

        return false;
    }

    void SendAck(IWTSVirtualChannel* channel, const BYTE* buffer, ULONG size, bool forwarded)
    {
        if (channel == nullptr)
        {
            return;
        }

        char nonce[64] = {};
        if (!TryExtractNonce(buffer, size, nonce, sizeof(nonce)))
        {
            strcpy_s(nonce, "missing");
        }

        char ack[192] = {};
        sprintf_s(
            ack,
            "ack=pause-double-press\nstatus=%s\nnonce=%s",
            forwarded ? "forwarded" : "pipe-failed",
            nonce);

        const HRESULT hr = channel->Write(
            static_cast<ULONG>(strlen(ack)),
            reinterpret_cast<BYTE*>(ack),
            nullptr);

        if (FAILED(hr))
        {
            wchar_t message[128] = {};
            swprintf_s(message, L"failed to write DVC ACK. HRESULT=0x%08X", static_cast<unsigned int>(hr));
            Trace(message);
            return;
        }

        wchar_t message[128] = {};
        swprintf_s(message, L"DVC ACK sent. Status=%s", forwarded ? L"forwarded" : L"pipe-failed");
        Trace(message);
    }

    class ChannelCallback final : public IWTSVirtualChannelCallback
    {
    public:
        explicit ChannelCallback(IWTSVirtualChannel* channel) : _channel(channel)
        {
            if (_channel != nullptr)
            {
                _channel->AddRef();
            }

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
            wchar_t message[128] = {};
            swprintf_s(message, L"DVC data received. Bytes=%lu", cbSize);
            Trace(message);

            const bool forwarded = ForwardToHostPipe(pBuffer, cbSize);
            SendAck(_channel, pBuffer, cbSize, forwarded);
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE OnClose() override
        {
            return S_OK;
        }

    private:
        ~ChannelCallback()
        {
            if (_channel != nullptr)
            {
                _channel->Release();
                _channel = nullptr;
            }

            InterlockedDecrement(&g_objectCount);
        }

        long _refCount = 1;
        IWTSVirtualChannel* _channel = nullptr;
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

            wchar_t message[128] = {};
            swprintf_s(message, L"Initialize called. ProcessId=%lu", GetCurrentProcessId());
            Trace(message);

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
            IWTSVirtualChannel* channel,
            BSTR,
            BOOL* accept,
            IWTSVirtualChannelCallback** callback) override
        {
            if (accept == nullptr || callback == nullptr)
            {
                return E_POINTER;
            }

            auto channelCallback = new (std::nothrow) ChannelCallback(channel);
            if (channelCallback == nullptr)
            {
                *accept = FALSE;
                *callback = nullptr;
                return E_OUTOFMEMORY;
            }

            *accept = TRUE;
            *callback = channelCallback;
            Trace(L"new DVC channel accepted");
            PreconnectHostPipe();
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
        InitializeCriticalSection(&g_pipeLock);
        DisableThreadLibraryCalls(module);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        EnterCriticalSection(&g_pipeLock);
        CloseHostPipeNoLock();
        LeaveCriticalSection(&g_pipeLock);
        DeleteCriticalSection(&g_pipeLock);
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
