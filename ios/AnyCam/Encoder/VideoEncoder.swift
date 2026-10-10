import CoreMedia
import Foundation
import VideoToolbox

/// Hardware H.264 / HEVC encoder (VideoToolbox) configured for live streaming: real-time, no B-frames,
/// fixed GOP, BT.709 tags. Output is Annex-B (start codes) with parameter sets in front of every keyframe.
final class VideoEncoder {
    struct Config: Equatable {
        var width: Int32
        var height: Int32
        var fps: Int
        var bitrate: Int
        var codec: String // "h264" | "hevc"
        var keyframeInterval: Double
    }

    /// Encoded access unit (Annex-B), keyframe flag, presentation time in µs. Called on a VideoToolbox thread.
    var onEncoded: ((Data, Bool, Int64) -> Void)?

    private(set) var config: Config
    private var session: VTCompressionSession?
    private let lock = NSLock()
    private var forceKeyframe = true

    init?(config: Config) {
        self.config = config
        guard createSession() else { return nil }
    }

    deinit {
        invalidate()
    }

    /// The codec actually used (HEVC falls back to H.264 if the session can't be created).
    var codec: String { config.codec }

    private func createSession() -> Bool {
        if config.codec == "hevc", makeSession(codec: kCMVideoCodecType_HEVC, lowLatency: false) {
            return true
        }
        config.codec = "h264"
        // Low-latency rate control (H.264 only) keeps frame sizes even, which is what a live link wants.
        return makeSession(codec: kCMVideoCodecType_H264, lowLatency: true) || makeSession(codec: kCMVideoCodecType_H264, lowLatency: false)
    }

    private func makeSession(codec: CMVideoCodecType, lowLatency: Bool) -> Bool {
        var specification: CFDictionary?
        if lowLatency {
            specification = [kVTVideoEncoderSpecification_EnableLowLatencyRateControl: true] as CFDictionary
        }

        var created: VTCompressionSession?
        let status = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: config.width,
            height: config.height,
            codecType: codec,
            encoderSpecification: specification,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: nil,
            refcon: nil,
            compressionSessionOut: &created)
        guard status == noErr, let created else { return false }
        session = created

        set(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
        set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse)
        if codec == kCMVideoCodecType_HEVC {
            set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_HEVC_Main_AutoLevel)
        } else {
            set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_High_AutoLevel)
            set(kVTCompressionPropertyKey_H264EntropyMode, kVTH264EntropyMode_CABAC)
        }
        set(kVTCompressionPropertyKey_ExpectedFrameRate, NSNumber(value: config.fps))
        set(kVTCompressionPropertyKey_MaxKeyFrameInterval, NSNumber(value: max(1, Int(Double(config.fps) * config.keyframeInterval))))
        set(kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration, NSNumber(value: config.keyframeInterval))
        set(kVTCompressionPropertyKey_ColorPrimaries, kCVImageBufferColorPrimaries_ITU_R_709_2)
        set(kVTCompressionPropertyKey_TransferFunction, kCVImageBufferTransferFunction_ITU_R_709_2)
        set(kVTCompressionPropertyKey_YCbCrMatrix, kCVImageBufferYCbCrMatrix_ITU_R_709_2)
        applyBitrate(config.bitrate)

        VTCompressionSessionPrepareToEncodeFrames(created)
        return true
    }

    @discardableResult
    private func set(_ key: CFString, _ value: CFTypeRef?) -> OSStatus {
        guard let session else { return -1 }
        // Individual properties may be unsupported on some devices/modes; that's fine.
        return VTSessionSetProperty(session, key: key, value: value)
    }

    private func applyBitrate(_ bitrate: Int) {
        set(kVTCompressionPropertyKey_AverageBitRate, NSNumber(value: bitrate))
        // Allow short bursts of 1.5× the average over one second.
        let bytesPerSecond = NSNumber(value: bitrate * 3 / 2 / 8)
        set(kVTCompressionPropertyKey_DataRateLimits, [bytesPerSecond, NSNumber(value: 1)] as CFArray)
    }

    func setBitrate(_ bitrate: Int) {
        lock.lock()
        config.bitrate = bitrate
        lock.unlock()
        applyBitrate(bitrate)
    }

    func requestKeyframe() {
        lock.lock()
        forceKeyframe = true
        lock.unlock()
    }

    func encode(_ imageBuffer: CVImageBuffer, presentationTime: CMTime, duration: CMTime) {
        guard let session else { return }

        lock.lock()
        let keyframe = forceKeyframe
        forceKeyframe = false
        lock.unlock()

        let properties: CFDictionary? = keyframe
            ? [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary
            : nil

        _ = VTCompressionSessionEncodeFrame(
            session,
            imageBuffer: imageBuffer,
            presentationTimeStamp: presentationTime,
            duration: duration,
            frameProperties: properties,
            infoFlagsOut: nil
        ) { [weak self] status, _, sampleBuffer in
            guard status == noErr, let sampleBuffer, let self else { return }
            self.deliver(sampleBuffer)
        }
    }

    func invalidate() {
        guard let session else { return }
        VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(session)
        self.session = nil
    }

    // MARK: Output

    private func deliver(_ sampleBuffer: CMSampleBuffer) {
        guard let blockBuffer = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
        let keyframe = !Self.isNotSync(sampleBuffer)

        var output = Data()
        var nalLengthSize = 4
        if let format = CMSampleBufferGetFormatDescription(sampleBuffer) {
            let (parameterSets, headerLength) = Self.parameterSets(format)
            if headerLength > 0 {
                nalLengthSize = headerLength
            }
            if keyframe {
                output.append(parameterSets)
            }
        }

        // Copy the AVCC payload (length-prefixed NAL units) and convert it to Annex-B.
        let totalLength = CMBlockBufferGetDataLength(blockBuffer)
        guard totalLength > 0 else { return }
        var avcc = Data(count: totalLength)
        let copyStatus = avcc.withUnsafeMutableBytes { buffer -> OSStatus in
            guard let base = buffer.baseAddress else { return -1 }
            return CMBlockBufferCopyDataBytes(blockBuffer, atOffset: 0, dataLength: totalLength, destination: base)
        }
        guard copyStatus == noErr else { return }

        var offset = 0
        while offset + nalLengthSize <= avcc.count {
            var nalLength = 0
            for i in 0..<nalLengthSize {
                nalLength = (nalLength << 8) | Int(avcc[offset + i])
            }
            offset += nalLengthSize
            guard nalLength > 0, offset + nalLength <= avcc.count else { break }
            output.append(contentsOf: [0, 0, 0, 1])
            output.append(avcc.subdata(in: offset..<(offset + nalLength)))
            offset += nalLength
        }

        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let microseconds = pts.isValid ? Int64(CMTimeGetSeconds(pts) * 1_000_000) : 0
        onEncoded?(output, keyframe, microseconds)
    }

    private static func isNotSync(_ sampleBuffer: CMSampleBuffer) -> Bool {
        guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false),
              CFArrayGetCount(attachments) > 0 else {
            return false
        }
        let attachment = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFDictionary.self)
        return CFDictionaryContainsKey(attachment, Unmanaged.passUnretained(kCMSampleAttachmentKey_NotSync).toOpaque())
    }

    /// VPS/SPS/PPS as Annex-B, plus the NAL length-prefix size used by the bitstream.
    private static func parameterSets(_ format: CMFormatDescription) -> (Data, Int) {
        let hevc = CMFormatDescriptionGetMediaSubType(format) == kCMVideoCodecType_HEVC
        var count = 0
        var headerLength: Int32 = 0
        let countStatus: OSStatus
        if hevc {
            countStatus = CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
                format, parameterSetIndex: 0, parameterSetPointerOut: nil, parameterSetSizeOut: nil,
                parameterSetCountOut: &count, nalUnitHeaderLengthOut: &headerLength)
        } else {
            countStatus = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                format, parameterSetIndex: 0, parameterSetPointerOut: nil, parameterSetSizeOut: nil,
                parameterSetCountOut: &count, nalUnitHeaderLengthOut: &headerLength)
        }
        guard countStatus == noErr else { return (Data(), 0) }

        var output = Data()
        for index in 0..<count {
            var pointer: UnsafePointer<UInt8>?
            var size = 0
            let status: OSStatus
            if hevc {
                status = CMVideoFormatDescriptionGetHEVCParameterSetAtIndex(
                    format, parameterSetIndex: index, parameterSetPointerOut: &pointer, parameterSetSizeOut: &size,
                    parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
            } else {
                status = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                    format, parameterSetIndex: index, parameterSetPointerOut: &pointer, parameterSetSizeOut: &size,
                    parameterSetCountOut: nil, nalUnitHeaderLengthOut: nil)
            }
            guard status == noErr, let pointer, size > 0 else { continue }
            output.append(contentsOf: [0, 0, 0, 1])
            output.append(pointer, count: size)
        }
        return (output, Int(headerLength))
    }
}
