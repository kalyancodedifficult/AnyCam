import AVFoundation
import UIKit

extension CameraController {
    // MARK: Settings from the PC (`set`)

    /// Applies the keys present in a `set` message. Every value is clamped to what the device supports:
    /// AVFoundation raises exceptions (crashes) on out-of-range values.
    func apply(_ message: JSON) {
        sessionQueue.async {
            guard let device = self.device else { return }
            do {
                try device.lockForConfiguration()
            } catch {
                return
            }
            defer { device.unlockForConfiguration() }

            if let zoom = message.double("zoom") {
                let maximum = min(device.maxAvailableVideoZoomFactor, device.activeFormat.videoMaxZoomFactor)
                let factor = CGFloat(min(max(zoom, Double(device.minAvailableVideoZoomFactor)), Double(maximum)))
                if let rate = message.double("zoomRate"), rate > 0 {
                    device.ramp(toVideoZoomFactor: factor, withRate: Float(min(rate, 20)))
                } else {
                    device.videoZoomFactor = factor
                }
            }

            if let mode = message.string("focusMode") {
                self.setFocusMode(mode, lensPosition: message.double("lensPosition"), on: device)
            } else if let position = message.double("lensPosition") {
                self.setFocusMode("manual", lensPosition: position, on: device)
            }
            if let smooth = message.bool("smoothFocus"), device.isSmoothAutoFocusSupported {
                device.isSmoothAutoFocusEnabled = smooth
            }
            if let faces = message.bool("faceDriven") {
                device.automaticallyAdjustsFaceDrivenAutoFocusEnabled = false
                device.isFaceDrivenAutoFocusEnabled = faces
                device.automaticallyAdjustsFaceDrivenAutoExposureEnabled = false
                device.isFaceDrivenAutoExposureEnabled = faces
            }

            if let bias = message.double("ev") {
                let clamped = min(max(Float(bias), device.minExposureTargetBias), device.maxExposureTargetBias)
                device.setExposureTargetBias(clamped, completionHandler: nil)
            }
            let mode = message.string("exposureMode")
            if mode != nil || message["iso"] != nil || message["shutter"] != nil {
                self.setExposure(mode: mode ?? self.exposureModeName, iso: message.double("iso"), shutter: message.double("shutter"), on: device)
            }

            if let mode = message.string("wbMode") {
                self.setWhiteBalance(mode: mode, temperature: message.double("temperature"), tint: message.double("tint"), on: device)
            } else if message["temperature"] != nil || message["tint"] != nil {
                self.setWhiteBalance(mode: "manual", temperature: message.double("temperature"), tint: message.double("tint"), on: device)
            }

            if let level = message.double("torch"), device.hasTorch, device.isTorchAvailable {
                if level <= 0.01 {
                    if device.isTorchModeSupported(.off) {
                        device.torchMode = .off
                    }
                } else {
                    try? device.setTorchModeOn(level: min(Float(level), AVCaptureDevice.maxAvailableTorchLevel))
                }
            }

            if let hdr = message.bool("hdr"), device.activeFormat.isVideoHDRSupported {
                device.automaticallyAdjustsVideoHDREnabled = false
                device.isVideoHDREnabled = hdr
            }
            if let boost = message.bool("lowLightBoost"), device.isLowLightBoostSupported {
                device.automaticallyEnablesLowLightBoostWhenAvailable = boost
            }
            if let stabilization = message.string("stabilization") {
                self.applyStabilization(named: stabilization)
            }

            self.delegate?.cameraStateDidChange(self)
        }
    }

    private func setFocusMode(_ name: String, lensPosition: Double?, on device: AVCaptureDevice) {
        switch name {
        case "continuous":
            guard device.isFocusModeSupported(.continuousAutoFocus) else { return }
            device.focusMode = .continuousAutoFocus
        case "auto":
            guard device.isFocusModeSupported(.autoFocus) else { return }
            device.focusMode = .autoFocus
        case "locked":
            guard device.isFocusModeSupported(.locked) else { return }
            device.focusMode = .locked
        case "manual":
            guard device.isLockingFocusWithCustomLensPositionSupported else { return }
            let position = Float(min(max(lensPosition ?? Double(device.lensPosition), 0), 1))
            device.setFocusModeLocked(lensPosition: position, completionHandler: nil)
        default:
            return
        }
        focusModeName = name
    }

    private func clampedISO(_ iso: Float, _ device: AVCaptureDevice) -> Float {
        min(max(iso, device.activeFormat.minISO), device.activeFormat.maxISO)
    }

    /// Exposure durations are limited by the format and by the frame time (we keep the frame rate locked).
    private func clampedDuration(_ seconds: Double, _ device: AVCaptureDevice) -> CMTime {
        let minimum = CMTimeGetSeconds(device.activeFormat.minExposureDuration)
        var maximum = CMTimeGetSeconds(device.activeFormat.maxExposureDuration)
        let frame = CMTimeGetSeconds(device.activeVideoMaxFrameDuration)
        if frame.isFinite, frame > 0 {
            maximum = min(maximum, frame)
        }
        let value = min(max(seconds, minimum), maximum)
        return CMTime(seconds: value, preferredTimescale: 1_000_000)
    }

    private func setExposure(mode: String, iso: Double?, shutter: Double?, on device: AVCaptureDevice) {
        switch mode {
        case "auto":
            stopShutterPriority()
            guard device.isExposureModeSupported(.continuousAutoExposure) else { return }
            device.exposureMode = .continuousAutoExposure
        case "locked":
            stopShutterPriority()
            guard device.isExposureModeSupported(.locked) else { return }
            device.exposureMode = .locked
        case "manual":
            stopShutterPriority()
            guard device.isExposureModeSupported(.custom) else { return }
            let duration = clampedDuration(shutter ?? CMTimeGetSeconds(device.exposureDuration), device)
            let value = clampedISO(Float(iso ?? Double(device.iso)), device)
            device.setExposureModeCustom(duration: duration, iso: value, completionHandler: nil)
        case "shutterPriority":
            guard device.isExposureModeSupported(.custom) else { return }
            shutterPriorityDuration = clampedDuration(shutter ?? CMTimeGetSeconds(shutterPriorityDuration), device)
            device.setExposureModeCustom(duration: shutterPriorityDuration, iso: clampedISO(device.iso, device), completionHandler: nil)
            startShutterPriority()
        default:
            return
        }
        exposureModeName = mode
    }

    /// Shutter priority: the shutter stays fixed (e.g. 1/50 s to avoid flicker under 50 Hz lights) and
    /// this loop steers ISO from the camera's metering, which iOS doesn't offer as a built-in mode.
    private func startShutterPriority() {
        guard shutterPriorityTimer == nil else { return }
        let timer = DispatchSource.makeTimerSource(queue: sessionQueue)
        timer.schedule(deadline: .now() + 0.1, repeating: 0.1)
        timer.setEventHandler { [weak self] in
            self?.shutterPriorityStep()
        }
        timer.resume()
        shutterPriorityTimer = timer
    }

    func stopShutterPriority() {
        shutterPriorityTimer?.cancel()
        shutterPriorityTimer = nil
    }

    private func shutterPriorityStep() {
        guard exposureModeName == "shutterPriority", let device, device.exposureMode == .custom else { return }
        let offset = device.exposureTargetOffset // EV the current exposure is above (+) or below (−) the target
        guard offset.isFinite, abs(offset) > 0.05 else { return }
        let target = clampedISO(device.iso * powf(2, -offset * 0.6), device)
        guard abs(target - device.iso) / max(device.iso, 1) > 0.02 else { return }
        do {
            try device.lockForConfiguration()
            device.setExposureModeCustom(duration: shutterPriorityDuration, iso: target, completionHandler: nil)
            device.unlockForConfiguration()
        } catch {
            return
        }
    }

    private func setWhiteBalance(mode: String, temperature: Double?, tint: Double?, on device: AVCaptureDevice) {
        switch mode {
        case "auto":
            guard device.isWhiteBalanceModeSupported(.continuousAutoWhiteBalance) else { return }
            device.whiteBalanceMode = .continuousAutoWhiteBalance
        case "locked":
            guard device.isWhiteBalanceModeSupported(.locked) else { return }
            device.whiteBalanceMode = .locked
        case "manual":
            guard device.isLockingWhiteBalanceWithCustomDeviceGainsSupported else { return }
            let current = device.temperatureAndTintValues(for: device.deviceWhiteBalanceGains)
            let kelvin = Float(min(max(temperature ?? Double(current.temperature), 2000), 10000))
            let tintValue = Float(min(max(tint ?? Double(current.tint), -150), 150))
            var gains = device.deviceWhiteBalanceGains(for: AVCaptureDevice.WhiteBalanceTemperatureAndTintValues(temperature: kelvin, tint: tintValue))
            let maxGain = device.maxWhiteBalanceGain
            gains.redGain = min(max(gains.redGain, 1), maxGain)
            gains.greenGain = min(max(gains.greenGain, 1), maxGain)
            gains.blueGain = min(max(gains.blueGain, 1), maxGain)
            device.setWhiteBalanceModeLocked(with: gains, completionHandler: nil)
        default:
            return
        }
        whiteBalanceModeName = mode
    }

    // MARK: Tap to focus / expose

    /// `x`, `y` are normalized coordinates in the image we send (after rotation).
    func focus(x: Double, y: Double, focus: Bool, expose: Bool) {
        sessionQueue.async {
            guard let device = self.device else { return }
            let point = self.devicePoint(x: min(max(x, 0), 1), y: min(max(y, 0), 1))
            do {
                try device.lockForConfiguration()
            } catch {
                return
            }
            defer { device.unlockForConfiguration() }

            if focus, device.isFocusPointOfInterestSupported {
                device.focusPointOfInterest = point
                if self.focusModeName == "continuous", device.isFocusModeSupported(.continuousAutoFocus) {
                    device.focusMode = .continuousAutoFocus
                } else if device.isFocusModeSupported(.autoFocus) {
                    device.focusMode = .autoFocus // focus once, then hold
                    self.focusModeName = "auto"
                }
            }
            if expose, device.isExposurePointOfInterestSupported, self.exposureModeName == "auto" || self.exposureModeName == "locked" {
                device.exposurePointOfInterest = point
                if self.exposureModeName == "auto", device.isExposureModeSupported(.continuousAutoExposure) {
                    device.exposureMode = .continuousAutoExposure
                } else if device.isExposureModeSupported(.autoExpose) {
                    device.exposureMode = .autoExpose // meter once, then lock
                }
            }
            self.delegate?.cameraStateDidChange(self)
        }
    }

    /// Converts a point in the rotated output image to the sensor's coordinate space
    /// ((0,0) top-left of the unrotated landscape buffer), undoing the clockwise rotation.
    private func devicePoint(x: Double, y: Double) -> CGPoint {
        switch Int(rotationAngle.rounded()) % 360 {
        case 90: return CGPoint(x: y, y: 1 - x)
        case 180: return CGPoint(x: 1 - x, y: 1 - y)
        case 270: return CGPoint(x: 1 - y, y: x)
        default: return CGPoint(x: x, y: y)
        }
    }

    // MARK: Reporting

    /// The `caps` message.
    func capabilities() -> JSON {
        let devices = Self.availableDevices()
        let wide = devices.first { Self.lensKind($0) == "wide" }
        let cameras: [JSON] = devices.map { device in
            let format = device.activeFormat
            return [
                "id": device.uniqueID,
                "name": device.localizedName,
                "position": device.position == .front ? "front" : "back",
                "lens": Self.lensKind(device),
                "zoomFactor": Self.zoomFactor(device, wide: wide),
                "formats": Self.formatSummary(device),
                "minISO": Double(format.minISO),
                "maxISO": Double(format.maxISO),
                "minShutter": CMTimeGetSeconds(format.minExposureDuration),
                "maxShutter": CMTimeGetSeconds(format.maxExposureDuration),
                "minZoom": Double(device.minAvailableVideoZoomFactor),
                "maxZoom": Double(min(device.maxAvailableVideoZoomFactor, format.videoMaxZoomFactor)),
                "zoomSwitchOver": device.virtualDeviceSwitchOverVideoZoomFactors.map { $0.doubleValue },
                "hasTorch": device.hasTorch,
                "focus": device.isFocusModeSupported(.continuousAutoFocus) || device.isFocusModeSupported(.autoFocus),
                "manualFocus": device.isLockingFocusWithCustomLensPositionSupported,
                "stabilization": Self.stabilizationNames(for: format),
                "maxWBGain": Double(device.maxWhiteBalanceGain),
                "lowLightBoost": device.isLowLightBoostSupported,
            ]
        }
        return [
            "t": "caps",
            "cameras": cameras,
            "codecs": ["h264", "hevc"],
            "audio": true,
            "photo": true,
        ]
    }

    /// 16:9 4:2:0 sizes the lens can stream, with the highest frame rate for each.
    static func formatSummary(_ device: AVCaptureDevice) -> [JSON] {
        var best: [String: (width: Int, height: Int, fps: Double, hdr: Bool)] = [:]
        for format in device.formats {
            let subtype = CMFormatDescriptionGetMediaSubType(format.formatDescription)
            guard subtype == kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange || subtype == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange else { continue }
            let dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            let width = Int(dimensions.width)
            let height = Int(dimensions.height)
            guard width * 9 == height * 16, width >= 1280 else { continue }
            let fps = format.videoSupportedFrameRateRanges.map(\.maxFrameRate).max() ?? 30
            let key = "\(width)x\(height)"
            let previous = best[key]
            best[key] = (width, height, max(fps, previous?.fps ?? 0), format.isVideoHDRSupported || (previous?.hdr ?? false))
        }
        return best.values
            .sorted { $0.width < $1.width }
            .map { ["width": $0.width, "height": $0.height, "maxFps": $0.fps, "hdr": $0.hdr] }
    }

    /// The camera part of the `state` message (session queue).
    func stateFields() -> JSON {
        var state: JSON = [
            "streaming": isRunning && session.isRunning,
            "width": settings.width,
            "height": settings.height,
            "fps": settings.fps,
            "orientation": settings.orientation,
            "audio": settings.audio,
            "interrupted": session.isInterrupted,
        ]
        guard let device else { return state }
        let whiteBalance = device.temperatureAndTintValues(for: device.deviceWhiteBalanceGains)
        state["camera"] = device.uniqueID
        state["zoom"] = Double(device.videoZoomFactor)
        state["focusMode"] = focusModeName
        state["lensPosition"] = Double(device.lensPosition)
        state["smoothFocus"] = device.isSmoothAutoFocusEnabled
        state["faceDriven"] = device.isFaceDrivenAutoFocusEnabled
        state["exposureMode"] = exposureModeName
        state["iso"] = Double(device.iso)
        state["shutter"] = CMTimeGetSeconds(device.exposureDuration)
        state["ev"] = Double(device.exposureTargetBias)
        state["exposureOffset"] = Double(device.exposureTargetOffset)
        state["wbMode"] = whiteBalanceModeName
        state["temperature"] = Double(whiteBalance.temperature)
        state["tint"] = Double(whiteBalance.tint)
        state["torch"] = device.hasTorch ? Double(device.torchLevel) : 0
        state["stabilization"] = stabilizationName
        state["hdr"] = device.activeFormat.isVideoHDRSupported && device.isVideoHDREnabled
        state["lowLightBoost"] = device.isLowLightBoostSupported && device.automaticallyEnablesLowLightBoostWhenAvailable
        return state
    }
}
