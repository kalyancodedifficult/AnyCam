#pragma once

namespace anycam
{
    class MediaSource;

    // The COM object registered under CLSID_AnyCamVCam. The Frame Server creates it and calls
    // ActivateObject to get the media source. IMFAttributes calls go to an internal store.
    class Activator : public Microsoft::WRL::RuntimeClass<
                          Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
                          Microsoft::WRL::ChainInterfaces<IMFActivate, IMFAttributes>,
                          Microsoft::WRL::FtmBase>
    {
    public:
        Activator();
        ~Activator() override; // defined in the .cpp where MediaSource is complete

        HRESULT RuntimeClassInitialize();

        // IMFActivate
        STDMETHODIMP ActivateObject(REFIID riid, void** object) override;
        STDMETHODIMP ShutdownObject() override;
        STDMETHODIMP DetachObject() override;

        // IMFAttributes
        STDMETHODIMP GetItem(REFGUID key, PROPVARIANT* val) override { return _attributes->GetItem(key, val); }
        STDMETHODIMP GetItemType(REFGUID key, MF_ATTRIBUTE_TYPE* type) override { return _attributes->GetItemType(key, type); }
        STDMETHODIMP CompareItem(REFGUID key, REFPROPVARIANT val, BOOL* result) override { return _attributes->CompareItem(key, val, result); }
        STDMETHODIMP Compare(IMFAttributes* theirs, MF_ATTRIBUTES_MATCH_TYPE matchType, BOOL* result) override { return _attributes->Compare(theirs, matchType, result); }
        STDMETHODIMP GetUINT32(REFGUID key, UINT32* val) override { return _attributes->GetUINT32(key, val); }
        STDMETHODIMP GetUINT64(REFGUID key, UINT64* val) override { return _attributes->GetUINT64(key, val); }
        STDMETHODIMP GetDouble(REFGUID key, double* val) override { return _attributes->GetDouble(key, val); }
        STDMETHODIMP GetGUID(REFGUID key, GUID* val) override { return _attributes->GetGUID(key, val); }
        STDMETHODIMP GetStringLength(REFGUID key, UINT32* length) override { return _attributes->GetStringLength(key, length); }
        STDMETHODIMP GetString(REFGUID key, LPWSTR val, UINT32 size, UINT32* length) override { return _attributes->GetString(key, val, size, length); }
        STDMETHODIMP GetAllocatedString(REFGUID key, LPWSTR* val, UINT32* length) override { return _attributes->GetAllocatedString(key, val, length); }
        STDMETHODIMP GetBlobSize(REFGUID key, UINT32* size) override { return _attributes->GetBlobSize(key, size); }
        STDMETHODIMP GetBlob(REFGUID key, UINT8* buffer, UINT32 bufferSize, UINT32* blobSize) override { return _attributes->GetBlob(key, buffer, bufferSize, blobSize); }
        STDMETHODIMP GetAllocatedBlob(REFGUID key, UINT8** buffer, UINT32* size) override { return _attributes->GetAllocatedBlob(key, buffer, size); }
        STDMETHODIMP GetUnknown(REFGUID key, REFIID riid, LPVOID* object) override { return _attributes->GetUnknown(key, riid, object); }
        STDMETHODIMP SetItem(REFGUID key, REFPROPVARIANT val) override { return _attributes->SetItem(key, val); }
        STDMETHODIMP DeleteItem(REFGUID key) override { return _attributes->DeleteItem(key); }
        STDMETHODIMP DeleteAllItems() override { return _attributes->DeleteAllItems(); }
        STDMETHODIMP SetUINT32(REFGUID key, UINT32 val) override { return _attributes->SetUINT32(key, val); }
        STDMETHODIMP SetUINT64(REFGUID key, UINT64 val) override { return _attributes->SetUINT64(key, val); }
        STDMETHODIMP SetDouble(REFGUID key, double val) override { return _attributes->SetDouble(key, val); }
        STDMETHODIMP SetGUID(REFGUID key, REFGUID val) override { return _attributes->SetGUID(key, val); }
        STDMETHODIMP SetString(REFGUID key, LPCWSTR val) override { return _attributes->SetString(key, val); }
        STDMETHODIMP SetBlob(REFGUID key, const UINT8* buffer, UINT32 size) override { return _attributes->SetBlob(key, buffer, size); }
        STDMETHODIMP SetUnknown(REFGUID key, IUnknown* val) override { return _attributes->SetUnknown(key, val); }
        STDMETHODIMP LockStore() override { return _attributes->LockStore(); }
        STDMETHODIMP UnlockStore() override { return _attributes->UnlockStore(); }
        STDMETHODIMP GetCount(UINT32* count) override { return _attributes->GetCount(count); }
        STDMETHODIMP GetItemByIndex(UINT32 index, GUID* key, PROPVARIANT* val) override { return _attributes->GetItemByIndex(index, key, val); }
        STDMETHODIMP CopyAllItems(IMFAttributes* destination) override { return _attributes->CopyAllItems(destination); }

    private:
        CriticalSection _lock;
        Microsoft::WRL::ComPtr<IMFAttributes> _attributes;
        Microsoft::WRL::ComPtr<MediaSource> _source;
    };
}
