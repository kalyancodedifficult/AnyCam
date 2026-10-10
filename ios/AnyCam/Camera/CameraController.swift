import AVFoundation
import UIKit

protocol CameraControllerDelegate: AnyObject {
    /// A video frame (video queue).
    func camera(_ camera: CameraController, didOutputVideo sampleBuffer: CMSampleBuffer)
    /// A microphone buffer (audio queue).
    func camera(_ camera: CameraController, didOutputAudio sampleBuffer: CMSampleBuffer)
    /// Settings or readings changed; the PC should get a fresh `state` (session queue).
    func cameraStateDidChange(_ camera: CameraController)
    /// Something the user should know about (session queue).
    func camera(_ camera: CameraController, event level: String, code: String, message: String)
}

/// Owns the AVCaptureSession. All configuration happens on `sessionQueue`.
final class CameraController: NSObject {
    let session = AVCaptureSession()
    let sessionQueue = DispatchQueue(label: "anycam.session", qos: .userInitiated)
    weak var delegate: CameraControllerDelegate?

    private let videoQueue = DispatchQueue(label: "anycam.video", qos: .userInteractive)
    private let audioQueue = DispatchQueue(label: "anycam.audio", qos: .userInteractive)
    private let videoOutput = AVCaptureVideoDataOutput()
    private let audioOutput = AVCaptureAudioDataOutput()
    let photoOutput = AVCapturePhotoOutput()
    private var videoInput: AVCaptureDeviceInput?
    private var audioInput: AVCaptureDeviceInput?
    private var photoDelegates: [Int64: PhotoCaptureDelegate] = [:]
    private var rotationCoordinator: AVCaptureDevice.RotationCoordinator?
    private var rotationObservation: NSKeyValueObservation?
    private var observers: [NSObjectProtocol] = []

    // Settings and modes that the device doesn't report directly (session queue).
    private(set) var device: AVCaptureDevice?
    private(set) var settings = StreamSettings()
    private(set) var rotationAngle: CGFloat = 0
    private(set) var isRunning = false
    var focusModeName = "continuous"
    var exposureModeName = "auto"
    var whiteBalanceModeName = "auto"
    var stabilizationName = "off"
    var shutterPriorityDuration = CMTime(value: 1, timescale: 50)
    var shutterPriorityTimer: DispatchSourceTimer?

    override init() {
        super.init()
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: AVCaptureSession.wasInterruptedNotification, object: session, queue: nil) { [weak self] notification in
            guard let self else { return }
            var reason = "The camera was interrupted."
            if let value = notification.userInfo?[AVCaptureSessionInterruptionReasonKey] as? Int,
               let interruption = AVCaptureSession.InterruptionReason(rawValue: value) {
                switch interruption {
                case .videoDeviceNotAvailableInBackground:
                    reason = "AnyCam must stay open on the iPhone to stream."
                case .audioDeviceInUseByAnotherClient, .videoDeviceInUseByAnotherClient:
                    reason = "Another app is using the camera or microphone."
                case .videoDeviceNotAvailableWithMultipleForegroundApps:
                    reason = "The camera isn't available in Split View or Slide Over."
                case .videoDeviceNotAvailableDueToSystemPressure:
                    reason = "The iPhone is too hot; the camera paused."
                default:
                    break
                }
            }
            self.sessionQueue.async {
                self.delegate?.camera(self, event: "warning", code: "interrupted", message: reason)
                self.delegate?.cameraStateDidChange(self)
            }
        })
        observers.append(center.addObserver(forName: AVCaptureSession.interruptionEndedNotification, object: session, queue: nil) { [weak self] _ in
            guard let self else { return }
            self.sessionQueue.async {
                self.delegate?.camera(self, event: "info", code: "resumed", message: "The camera is back.")
                self.delegate?.cameraStateDidChange(self)
            }
        })
        observers.append(center.addObserver(forName: AVCaptureSession.runtimeErrorNotification, object: session, queue: nil) { [weak self] notification in
            guard let self else { return }
            let error = notification.userInfo?[AVCaptureSessionErrorKey] as? NSError
            self.sessionQueue.async {
                self.delegate?.camera(self, event: "warning", code: "cameraError", message: error?.localizedDescription ?? "Camera error")
                // Media services reset or similar: try to start again.
                if self.isRunning && !self.session.isRunning {
                    self.session.startRunning()
                }
            }
        })
    }

    deinit {
        observers.forEach { NotificationCenter.default.removeObserver($0) }
    }

    var isInterrupted: Bool { session.isInterrupted }

    // MARK: Devices

    /// Physical lenses: ultra-wide, wide and telephoto on the back, plus the front camera.
    static func availableDevices() -> [AVCaptureDevice] {
        let back = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.builtInUltraWideCamera, .builtInWideAngleCamera, .builtInTelephotoCamera],
            mediaType: .video, position: .back).devices
        let front = AVCaptureDevice.DiscoverySession(
            deviceTypes: [.builtInWideAngleCamera, .builtInTrueDepthCamera],
            mediaType: .video, position: .front).devices
        // Some front cameras appear twice (wide + TrueDepth); keep the plain one.
        let frontCamera = front.first { $0.deviceType == .builtInWideAngleCamera } ?? front.first
        return back + (frontCamera.map { [$0] } ?? [])
    }

    static func lensKind(_ device: AVCaptureDevice) -> String {
        if device.position == .front { return "front" }
        switch device.deviceType {
        case .builtInUltraWideCamera: return "ultraWide"
        case .builtInTelephotoCamera: return "telephoto"
        default: return "wide"
        }
    }

    /// Magnification relative to the main wide lens (0.5 for ultra-wide, 2/3/5 for telephoto).
    static func zoomFactor(_ device: AVCaptureDevice, wide: AVCaptureDevice?) -> Double {
        switch lensKind(device) {
        case "ultraWide":
            return 0.5
        case "telephoto":
            guard let wide else { return 2 }
            let wideFov = Double(wide.activeFormat.videoFieldOfView) * .pi / 180
            let teleFov = Double(device.activeFormat.videoFieldOfView) * .pi / 180
            guard wideFov > 0, teleFov > 0 else { return 2 }
            let ratio = tan(wideFov / 2) / tan(teleFov / 2)
            return (ratio * 2).rounded() / 2 // nearest 0.5×
        default:
            return 1
        }
    }

    // MARK: Session control

    /// Applies stream settings (lens, size, fps, orientation, audio) and starts the camera if needed.
    func start(with newSettings: StreamSettings) {
        sessionQueue.async {
            self.configure(newSettings)
            if !self.session.isRunning {
                self.session.startRunning()
            }
            self.isRunning = true
            self.delegate?.cameraStateDidChange(self)
        }
    }

    func stop() {
        sessionQueue.async {
            self.stopShutterPriority()
            if self.session.isRunning {
                self.session.stopRunning()
            }
            self.isRunning = false
            self.delegate?.cameraStateDidChange(self)
        }
    }

    /// Re-applies settings (e.g. a `configure` while streaming).
    func reconfigure(_ newSettings: StreamSettings) {
        sessionQueue.async {
            self.configure(newSettings)
            self.delegate?.cameraStateDidChange(self)
        }
    }

    private func configure(_ newSettings: StreamSettings) {
        let devices = Self.availableDevices()
        let chosen = devices.first { $0.uniqueID == newSettings.cameraId }
            ?? devices.first { $0.uniqueID == device?.uniqueID }
            ?? devices.first { Self.lensKind($0) == "wide" }
            ?? devices.first
        guard let chosen else {
            delegate?.camera(self, event: "error", code: "unsupported", message: "No camera found.")
            return
        }

        session.beginConfiguration()
        defer { session.commitConfiguration() }
        // We pick formats ourselves; a preset would override the frame rate on commit.
        if session.sessionPreset != .inputPriority, session.canSetSessionPreset(.inputPriority) {
            session.sessionPreset = .inputPriority
        }

        // Video input.
        if videoInput?.device != chosen {
            if let videoInput {
                session.removeInput(videoInput)
                self.videoInput = nil
            }
            do {
                let input = try AVCaptureDeviceInput(device: chosen)
                if session.canAddInput(input) {
                    session.addInput(input)
                    videoInput = input
                }
            } catch {
                delegate?.camera(self, event: "error", code: "cameraError", message: "Couldn't open the camera: \(error.localizedDescription)")
                return
            }
            stopShutterPriority()
            focusModeName = "continuous"
            exposureModeName = "auto"
            whiteBalanceModeName = "auto"
        }
        device = chosen

        // Audio input.
        if newSettings.audio && audioInput == nil, let microphone = AVCaptureDevice.default(for: .audio),
           let input = try? AVCaptureDeviceInput(device: microphone), session.canAddInput(input) {
            session.addInput(input)
            audioInput = input
        } else if !newSettings.audio, let audioInput {
            session.removeInput(audioInput)
            self.audioInput = nil
        }

        // Outputs (added once).
        if !session.outputs.contains(videoOutput) {
            videoOutput.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange]
            videoOutput.alwaysDiscardsLateVideoFrames = true
            videoOutput.setSampleBufferDelegate(self, queue: videoQueue)
            if session.canAddOutput(videoOutput) {
                session.addOutput(videoOutput)
            }
        }
        if !session.outputs.contains(audioOutput) {
            audioOutput.setSampleBufferDelegate(self, queue: audioQueue)
            if session.canAddOutput(audioOutput) {
                session.addOutput(audioOutput)
            }
        }
        if !session.outputs.contains(photoOutput), session.canAddOutput(photoOutput) {
            session.addOutput(photoOutput)
        }

        // Format and frame rate.
        let format = Self.bestFormat(for: chosen, width: newSettings.width, height: newSettings.height, fps: newSettings.fps)
        let fps = Self.supportedFps(newSettings.fps, format: format ?? chosen.activeFormat)
        do {
            try chosen.lockForConfiguration()
            if let format {
                chosen.activeFormat = format
            }
            let frameDuration = CMTime(value: 1, timescale: CMTimeScale(fps))
            chosen.activeVideoMinFrameDuration = frameDuration
            chosen.activeVideoMaxFrameDuration = frameDuration
            if chosen.isSmoothAutoFocusSupported && focusModeName == "continuous" {
                chosen.isSmoothAutoFocusEnabled = true // no visible "hunting" jumps in video
            }
            chosen.unlockForConfiguration()
        } catch {
            delegate?.camera(self, event: "warning", code: "cameraError", message: "Couldn't set the video format: \(error.localizedDescription)")
        }

        var applied = newSettings
        applied.cameraId = chosen.uniqueID
        let dimensions = CMVideoFormatDescriptionGetDimensions(chosen.activeFormat.formatDescription)
        applied.width = Int(dimensions.width)
        applied.height = Int(dimensions.height)
        applied.fps = fps
        settings = applied

        // Largest still photos this format supports (for thumbnails).
        if let largest = chosen.activeFormat.supportedMaxPhotoDimensions.max(by: { $0.width * $0.height < $1.width * $1.height }) {
            photoOutput.maxPhotoDimensions = largest
        }

        configureRotation(for: chosen, orientation: applied.orientation)
        applyStabilization(named: stabilizationName)
        if let connection = videoOutput.connection(with: .video) {
            if connection.isVideoMirroringSupported {
                connection.automaticallyAdjustsVideoMirroring = false
                connection.isVideoMirrored = false // never mirror what we send; the PC can mirror its preview
            }
        }
    }

    /// Picks the 4:2:0 format matching the size that supports the frame rate (video range preferred).
    static func bestFormat(for device: AVCaptureDevice, width: Int, height: Int, fps: Int) -> AVCaptureDevice.Format? {
        let videoRange = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange
        let fullRange = kCVPixelFormatType_420YpCbCr8BiPlanarFullRange

        func score(_ format: AVCaptureDevice.Format) -> Int? {
            let subtype = CMFormatDescriptionGetMediaSubType(format.formatDescription)
            guard subtype == videoRange || subtype == fullRange else { return nil }
            let dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            guard Int(dimensions.width) == width, Int(dimensions.height) == height else { return nil }
            let supportsFps = format.videoSupportedFrameRateRanges.contains { $0.minFrameRate <= Double(fps) && Double(fps) <= $0.maxFrameRate }
            var points = supportsFps ? 100 : 0
            points += subtype == videoRange ? 10 : 0
            points += format.isVideoHDRSupported ? 2 : 0
            if format.isVideoStabilizationModeSupported(.standard) { points += 1 }
            return points
        }

        let scored = device.formats.compactMap { format in score(format).map { (format, $0) } }
        if let best = scored.max(by: { $0.1 < $1.1 }), best.1 >= 100 {
            return best.0
        }
        if let best = scored.max(by: { $0.1 < $1.1 }) {
            return best.0 // right size, lower max frame rate
        }

        // No exact size: the closest 4:2:0 format by pixel count that supports the frame rate.
        let target = width * height
        return device.formats
            .filter { format in
                let subtype = CMFormatDescriptionGetMediaSubType(format.formatDescription)
                return (subtype == videoRange || subtype == fullRange) &&
                    format.videoSupportedFrameRateRanges.contains { Double(fps) <= $0.maxFrameRate }
            }
            .min { lhs, rhs in
                let l = CMVideoFormatDescriptionGetDimensions(lhs.formatDescription)
                let r = CMVideoFormatDescriptionGetDimensions(rhs.formatDescription)
                return abs(Int(l.width) * Int(l.height) - target) < abs(Int(r.width) * Int(r.height) - target)
            }
    }

    static func supportedFps(_ fps: Int, format: AVCaptureDevice.Format) -> Int {
        let maxFps = format.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? 30
        let minFps = format.videoSupportedFrameRateRanges.map(\.minFrameRate).min() ?? 1
        return Int(min(max(Double(fps), minFps), maxFps).rounded(.down))
    }

    // MARK: Orientation

    private func configureRotation(for device: AVCaptureDevice, orientation: String) {
        rotationObservation?.invalidate()
        rotationObservation = nil
        rotationCoordinator = nil

        switch orientation {
        case "portrait":
            setRotation(90)
        case "landscape":
            setRotation(0)
        default:
            // Follow how the phone is physically mounted, keeping the horizon level.
            let coordinator = AVCaptureDevice.RotationCoordinator(device: device, previewLayer: nil)
            rotationCoordinator = coordinator
            setRotation(coordinator.videoRotationAngleForHorizonLevelCapture)
            rotationObservation = coordinator.observe(\.videoRotationAngleForHorizonLevelCapture, options: [.new]) { [weak self] coordinator, _ in
                guard let self else { return }
                let angle = coordinator.videoRotationAngleForHorizonLevelCapture
                self.sessionQueue.async {
                    self.setRotation(angle)
                }
            }
        }
    }

    private func setRotation(_ angle: CGFloat) {
        rotationAngle = angle
        if let connection = videoOutput.connection(with: .video), connection.isVideoRotationAngleSupported(angle) {
            connection.videoRotationAngle = angle
        }
        if let connection = photoOutput.connection(with: .video), connection.isVideoRotationAngleSupported(angle) {
            connection.videoRotationAngle = angle
        }
    }

    // MARK: Stabilization

    func applyStabilization(named name: String) {
        guard let device, let connection = videoOutput.connection(with: .video), connection.isVideoStabilizationSupported else {
            stabilizationName = "off"
            return
        }
        let mode = Self.stabilizationMode(named: name)
        if device.activeFormat.isVideoStabilizationModeSupported(mode) {
            connection.preferredVideoStabilizationMode = mode
            stabilizationName = name
        } else {
            connection.preferredVideoStabilizationMode = .off
            stabilizationName = "off"
        }
    }

    static func stabilizationMode(named name: String) -> AVCaptureVideoStabilizationMode {
        switch name {
        case "standard": return .standard
        case "cinematic": return .cinematic
        case "cinematicExtended": return .cinematicExtended
        case "previewOptimized": return .previewOptimized
        case "auto": return .auto
        case "cinematicExtendedEnhanced":
            if #available(iOS 18.0, *) {
                return .cinematicExtendedEnhanced
            }
            return .cinematicExtended
        default: return .off
        }
    }

    static func stabilizationNames(for format: AVCaptureDevice.Format) -> [String] {
        var names = ["off"]
        for name in ["standard", "cinematic", "cinematicExtended", "cinematicExtendedEnhanced", "previewOptimized"] {
            let mode = stabilizationMode(named: name)
            if mode != .off, !(name == "cinematicExtendedEnhanced" && mode == .cinematicExtended), format.isVideoStabilizationModeSupported(mode) {
                names.append(name)
            }
        }
        return names
    }

    // MARK: Photo

    /// Captures a full-resolution still. Completion runs on the session queue.
    func capturePhoto(format: String, completion: @escaping (Data?, Int, Int, String) -> Void) {
        sessionQueue.async {
            guard self.session.isRunning, self.session.outputs.contains(self.photoOutput) else {
                completion(nil, 0, 0, format)
                return
            }
            let useHeic = format == "heic" && self.photoOutput.availablePhotoCodecTypes.contains(.hevc)
            let settings = AVCapturePhotoSettings(format: [AVVideoCodecKey: useHeic ? AVVideoCodecType.hevc : AVVideoCodecType.jpeg])
            settings.maxPhotoDimensions = self.photoOutput.maxPhotoDimensions
            let id = settings.uniqueID
            let delegate = PhotoCaptureDelegate { [weak self] data, width, height in
                guard let self else { return }
                self.sessionQueue.async {
                    self.photoDelegates[id] = nil
                    completion(data, width, height, useHeic ? "heic" : "jpeg")
                }
            }
            self.photoDelegates[id] = delegate
            self.photoOutput.capturePhoto(with: settings, delegate: delegate)
        }
    }
}

extension CameraController: AVCaptureVideoDataOutputSampleBufferDelegate, AVCaptureAudioDataOutputSampleBufferDelegate {
    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        if output === videoOutput {
            delegate?.camera(self, didOutputVideo: sampleBuffer)
        } else if output === audioOutput {
            delegate?.camera(self, didOutputAudio: sampleBuffer)
        }
    }
}

/// Keeps the capture delegate alive until the photo is ready.
final class PhotoCaptureDelegate: NSObject, AVCapturePhotoCaptureDelegate {
    private let completion: (Data?, Int, Int) -> Void

    init(completion: @escaping (Data?, Int, Int) -> Void) {
        self.completion = completion
    }

    func photoOutput(_ output: AVCapturePhotoOutput, didFinishProcessingPhoto photo: AVCapturePhoto, error: Error?) {
        let dimensions = photo.resolvedSettings.photoDimensions
        completion(error == nil ? photo.fileDataRepresentation() : nil, Int(dimensions.width), Int(dimensions.height))
    }
}
