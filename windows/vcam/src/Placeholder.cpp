#include "pch.h"
#include "Placeholder.h"

namespace anycam
{
    const std::vector<uint8_t>& Placeholder::Get(uint32_t width, uint32_t height, PixelFormat format)
    {
        if (width != _width || height != _height || format != _format || _pixels.empty())
        {
            _width = width;
            _height = height;
            _format = format;
            Render();
        }
        return _pixels;
    }

    void Placeholder::Render()
    {
        std::vector<uint8_t> bgra(size_t(_width) * _height * 4);

        // Dark vertical gradient as the base, in case GDI isn't usable for some reason.
        for (uint32_t y = 0; y < _height; y++)
        {
            const uint8_t shade = static_cast<uint8_t>(18 + (24 * y) / (_height ? _height : 1));
            uint8_t* row = bgra.data() + size_t(y) * _width * 4;
            for (uint32_t x = 0; x < _width; x++)
            {
                row[x * 4 + 0] = static_cast<uint8_t>(shade + 6);
                row[x * 4 + 1] = shade;
                row[x * 4 + 2] = shade;
                row[x * 4 + 3] = 255;
            }
        }

        HDC dc = CreateCompatibleDC(nullptr);
        BITMAPINFO info{};
        info.bmiHeader.biSize = sizeof(info.bmiHeader);
        info.bmiHeader.biWidth = static_cast<LONG>(_width);
        info.bmiHeader.biHeight = -static_cast<LONG>(_height); // top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = BI_RGB;
        void* bits = nullptr;
        HBITMAP bitmap = dc ? CreateDIBSection(dc, &info, DIB_RGB_COLORS, &bits, nullptr, 0) : nullptr;
        if (bitmap && bits)
        {
            memcpy(bits, bgra.data(), bgra.size());
            HGDIOBJ oldBitmap = SelectObject(dc, bitmap);
            SetBkMode(dc, TRANSPARENT);

            const int unit = static_cast<int>((_width < _height ? _width : _height) / 12);
            HFONT titleFont = CreateFontW(-unit * 2, 0, 0, 0, FW_SEMIBOLD, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                          OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                                          DEFAULT_PITCH | FF_SWISS, L"Segoe UI");
            HFONT bodyFont = CreateFontW(-unit * 2 / 5, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE, DEFAULT_CHARSET,
                                         OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY,
                                         DEFAULT_PITCH | FF_SWISS, L"Segoe UI");

            RECT title{ 0, static_cast<LONG>(_height / 2) - unit * 2, static_cast<LONG>(_width), static_cast<LONG>(_height / 2) + unit / 4 };
            RECT body{ unit / 2, static_cast<LONG>(_height / 2) + unit / 2, static_cast<LONG>(_width) - unit / 2, static_cast<LONG>(_height) };

            HGDIOBJ oldFont = SelectObject(dc, titleFont);
            SetTextColor(dc, RGB(240, 240, 245));
            DrawTextW(dc, L"AnyCam", -1, &title, DT_CENTER | DT_BOTTOM | DT_SINGLELINE);

            SelectObject(dc, bodyFont);
            SetTextColor(dc, RGB(150, 150, 165));
            DrawTextW(dc, L"Start AnyCam on this PC and connect your phone", -1, &body, DT_CENTER | DT_TOP | DT_WORDBREAK);

            SelectObject(dc, oldFont);
            SelectObject(dc, oldBitmap);
            DeleteObject(titleFont);
            DeleteObject(bodyFont);
            GdiFlush();

            memcpy(bgra.data(), bits, bgra.size());
            // GDI leaves alpha at 0 where it drew text; we want opaque pixels.
            for (size_t i = 3; i < bgra.size(); i += 4)
                bgra[i] = 255;
        }
        if (bitmap)
            DeleteObject(bitmap);
        if (dc)
            DeleteDC(dc);

        if (_format == PixelFormat_BGRA)
        {
            _pixels = std::move(bgra);
        }
        else
        {
            _pixels.assign(size_t(_width) * _height * 3 / 2, 0);
            BgraToNv12(bgra.data(), _width, _height, _pixels.data());
        }
    }

    void BgraToNv12(const uint8_t* bgra, uint32_t width, uint32_t height, uint8_t* nv12)
    {
        // BT.709, limited range (Y 16..235, UV 16..240), fixed-point coefficients * 256.
        uint8_t* yPlane = nv12;
        uint8_t* uvPlane = nv12 + size_t(width) * height;
        for (uint32_t y = 0; y < height; y++)
        {
            const uint8_t* row = bgra + size_t(y) * width * 4;
            for (uint32_t x = 0; x < width; x++)
            {
                const int b = row[x * 4 + 0], g = row[x * 4 + 1], r = row[x * 4 + 2];
                yPlane[size_t(y) * width + x] = static_cast<uint8_t>(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
            }
        }
        for (uint32_t y = 0; y + 1 < height; y += 2)
        {
            const uint8_t* row0 = bgra + size_t(y) * width * 4;
            const uint8_t* row1 = row0 + size_t(width) * 4;
            uint8_t* uvRow = uvPlane + size_t(y / 2) * width;
            for (uint32_t x = 0; x + 1 < width; x += 2)
            {
                int r = 0, g = 0, b = 0;
                for (const uint8_t* p : { row0 + x * 4, row0 + x * 4 + 4, row1 + x * 4, row1 + x * 4 + 4 })
                {
                    b += p[0];
                    g += p[1];
                    r += p[2];
                }
                r /= 4;
                g /= 4;
                b /= 4;
                const int u = ((-26 * r - 87 * g + 112 * b + 128) >> 8) + 128;
                const int v = ((112 * r - 102 * g - 10 * b + 128) >> 8) + 128;
                uvRow[x] = static_cast<uint8_t>(u < 0 ? 0 : (u > 255 ? 255 : u));
                uvRow[x + 1] = static_cast<uint8_t>(v < 0 ? 0 : (v > 255 ? 255 : v));
            }
        }
    }
}
