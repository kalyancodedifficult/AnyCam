#pragma once

namespace anycam
{
    // Frame shown when AnyCam Desktop isn't feeding the camera (app closed or no phone yet).
    // Rendered once per size/format with GDI (works in the Frame Server's session-0 service process).
    class Placeholder
    {
    public:
        // Returns a tightly packed frame (stride = width for NV12, width * 4 for BGRA).
        const std::vector<uint8_t>& Get(uint32_t width, uint32_t height, PixelFormat format);
        uint32_t Stride() const { return _format == PixelFormat_BGRA ? _width * 4 : _width; }

    private:
        void Render();

        uint32_t _width = 0;
        uint32_t _height = 0;
        PixelFormat _format = PixelFormat_None;
        std::vector<uint8_t> _pixels;
    };

    // BT.709 limited-range conversion of a top-down BGRA image to tightly packed NV12.
    void BgraToNv12(const uint8_t* bgra, uint32_t width, uint32_t height, uint8_t* nv12);
}
