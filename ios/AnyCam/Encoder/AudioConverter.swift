import AVFoundation
import CoreMedia

/// Converts microphone sample buffers (whatever the hardware delivers: Float32 or Int16, 44.1 or
/// 48 kHz, mono or stereo) to the protocol's audio format: signed 16-bit little-endian, 48 kHz, mono.
final class AudioConverter {
    static let sampleRate = 48_000
    static let channels = 1

    private let outputFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: Double(AudioConverter.sampleRate),
                                             channels: AVAudioChannelCount(AudioConverter.channels), interleaved: true)!
    private var inputFormat: AVAudioFormat?
    private var converter: AVAudioConverter?
    private var lastDescription = AudioStreamBasicDescription()

    /// Returns PCM bytes for the buffer, or nil if it couldn't be converted.
    func convert(_ sampleBuffer: CMSampleBuffer) -> Data? {
        guard let description = CMSampleBufferGetFormatDescription(sampleBuffer),
              let streamDescription = CMAudioFormatDescriptionGetStreamBasicDescription(description) else {
            return nil
        }

        var asbd = streamDescription.pointee
        if converter == nil || !Self.same(asbd, lastDescription) {
            guard let format = AVAudioFormat(streamDescription: &asbd),
                  let newConverter = AVAudioConverter(from: format, to: outputFormat) else {
                return nil
            }
            inputFormat = format
            converter = newConverter
            lastDescription = asbd
        }
        guard let inputFormat, let converter else { return nil }

        let frames = AVAudioFrameCount(CMSampleBufferGetNumSamples(sampleBuffer))
        guard frames > 0, let input = AVAudioPCMBuffer(pcmFormat: inputFormat, frameCapacity: frames) else { return nil }
        input.frameLength = frames
        let copyStatus = CMSampleBufferCopyPCMDataIntoAudioBufferList(sampleBuffer, at: 0, frameCount: Int32(frames), into: input.mutableAudioBufferList)
        guard copyStatus == noErr else { return nil }

        let ratio = outputFormat.sampleRate / inputFormat.sampleRate
        let capacity = AVAudioFrameCount(Double(frames) * ratio + 64)
        guard let output = AVAudioPCMBuffer(pcmFormat: outputFormat, frameCapacity: capacity) else { return nil }

        var provided = false
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, inputStatus in
            if provided {
                inputStatus.pointee = .noDataNow
                return nil
            }
            provided = true
            inputStatus.pointee = .haveData
            return input
        }
        guard status != .error, error == nil, output.frameLength > 0, let samples = output.int16ChannelData else { return nil }
        let byteCount = Int(output.frameLength) * AudioConverter.channels * MemoryLayout<Int16>.size
        return Data(bytes: samples[0], count: byteCount)
    }

    private static func same(_ a: AudioStreamBasicDescription, _ b: AudioStreamBasicDescription) -> Bool {
        a.mSampleRate == b.mSampleRate && a.mFormatID == b.mFormatID && a.mFormatFlags == b.mFormatFlags &&
            a.mBytesPerFrame == b.mBytesPerFrame && a.mChannelsPerFrame == b.mChannelsPerFrame && a.mBitsPerChannel == b.mBitsPerChannel
    }
}
