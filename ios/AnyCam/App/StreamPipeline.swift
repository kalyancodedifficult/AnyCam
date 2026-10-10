import AVFoundation
import Foundation

/// Glue between the camera and the connected PC: encodes video frames, converts audio, and sends both.
/// Camera callbacks arrive on the camera's video/audio queues; shared state is guarded by `lock`.
final class StreamPipeline: CameraControllerDelegate {
    /// Settings or readings changed (any thread).
    var onStateChange: (() -> Void)?
    /// level, code, message (any thread).
    var onEvent: ((String, String, String) -> Void)?
    /// codec, width, height of a newly started bitstream (video queue).
    var onVideoFormat: ((String, Int, Int) -> Void)?

    private let lock = NSLock()
    private var connection: HostConnection?
    private var settings = StreamSettings()
    private var encoder: VideoEncoder?
    private var restartEncoder = true
    private var audioAnnounced = false
    private let audioConverter = AudioConverter() // audio queue only

    /// Points the pipeline at a PC (or none). A fresh encoder starts with the next frame.
    func attach(_ connection: HostConnection?, settings: StreamSettings) {
        lock.lock()
        self.connection = connection
        self.settings = settings
        restartEncoder = true
        audioAnnounced = false
        lock.unlock()

        guard let connection else { return }
        connection.onKeyframeNeeded = { [weak self] in
            self?.requestKeyframe()
        }
        connection.onBitrateChange = { [weak self] bitrate in
            self?.currentEncoder()?.setBitrate(bitrate)
        }
        connection.setTargetBitrate(settings.bitrate)
    }

    /// New stream settings from the PC. Bitrate changes apply live; codec / frame-rate changes restart the encoder.
    func update(settings newSettings: StreamSettings) {
        lock.lock()
        let old = settings
        settings = newSettings
        if old.codec != newSettings.codec || old.fps != newSettings.fps || old.keyframeInterval != newSettings.keyframeInterval {
            restartEncoder = true
        }
        let encoder = self.encoder
        let connection = self.connection
        lock.unlock()

        if old.bitrate != newSettings.bitrate {
            encoder?.setBitrate(newSettings.bitrate)
            connection?.setTargetBitrate(newSettings.bitrate)
        }
    }

    func requestKeyframe() {
        currentEncoder()?.requestKeyframe()
    }

    /// Temporarily lowers the bitrate (e.g. when the phone gets hot).
    func setBitrate(_ bitrate: Int) {
        lock.lock()
        let connection = self.connection
        let encoder = self.encoder
        lock.unlock()
        connection?.setTargetBitrate(bitrate)
        encoder?.setBitrate(bitrate)
    }

    private func currentEncoder() -> VideoEncoder? {
        lock.lock()
        defer { lock.unlock() }
        return encoder
    }

    // MARK: CameraControllerDelegate

    func camera(_ camera: CameraController, didOutputVideo sampleBuffer: CMSampleBuffer) {
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        lock.lock()
        let connection = self.connection
        let settings = self.settings
        let restart = restartEncoder
        restartEncoder = false
        var encoder = self.encoder
        lock.unlock()

        guard let connection else { return }

        let width = Int32(CVPixelBufferGetWidth(pixelBuffer))
        let height = Int32(CVPixelBufferGetHeight(pixelBuffer))
        if restart || encoder == nil || encoder?.config.width != width || encoder?.config.height != height {
            // New connection, new settings, or the picture rotated (landscape ⇄ portrait).
            encoder?.invalidate()
            let config = VideoEncoder.Config(width: width, height: height, fps: settings.fps, bitrate: settings.bitrate,
                                             codec: settings.codec, keyframeInterval: settings.keyframeInterval)
            guard let created = VideoEncoder(config: config) else {
                lock.lock()
                self.encoder = nil
                lock.unlock()
                onEvent?("error", "encoderError", "The video encoder couldn't start at \(width)×\(height).")
                return
            }
            created.onEncoded = { [weak connection] data, keyframe, timestamp in
                connection?.sendVideo(data, keyframe: keyframe, timestamp: timestamp)
            }
            encoder = created

            lock.lock()
            self.encoder = created
            lock.unlock()

            connection.sendControl([
                "t": "video",
                "codec": created.codec,
                "width": Int(width),
                "height": Int(height),
                "fps": settings.fps,
                "bitrate": settings.bitrate,
                "colorSpace": "bt709",
                "range": "video",
            ])
            onVideoFormat?(created.codec, Int(width), Int(height))
            onStateChange?()
        }

        encoder?.encode(pixelBuffer,
                        presentationTime: CMSampleBufferGetPresentationTimeStamp(sampleBuffer),
                        duration: CMSampleBufferGetDuration(sampleBuffer))
    }

    func camera(_ camera: CameraController, didOutputAudio sampleBuffer: CMSampleBuffer) {
        lock.lock()
        let connection = self.connection
        let enabled = settings.audio
        let announce = !audioAnnounced
        if connection != nil && enabled {
            audioAnnounced = true
        }
        lock.unlock()

        guard let connection, enabled, let pcm = audioConverter.convert(sampleBuffer) else { return }
        if announce {
            connection.sendControl([
                "t": "audio",
                "sampleRate": AudioConverter.sampleRate,
                "channels": AudioConverter.channels,
                "format": "s16le",
            ])
        }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        connection.sendAudio(pcm, timestamp: Int64(CMTimeGetSeconds(pts) * 1_000_000))
    }

    func cameraStateDidChange(_ camera: CameraController) {
        onStateChange?()
    }

    func camera(_ camera: CameraController, event level: String, code: String, message: String) {
        onEvent?(level, code, message)
    }

    func shutdown() {
        lock.lock()
        let encoder = self.encoder
        self.encoder = nil
        connection = nil
        lock.unlock()
        encoder?.invalidate()
    }
}
