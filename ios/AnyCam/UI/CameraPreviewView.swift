import AVFoundation
import SwiftUI
import UIKit

/// Live preview of the capture session, kept upright as the phone (or its UI) rotates.
struct CameraPreviewView: UIViewRepresentable {
    let session: AVCaptureSession
    let cameraID: String?

    func makeUIView(context: Context) -> PreviewView {
        let view = PreviewView()
        view.backgroundColor = .black
        view.previewLayer.session = session
        view.previewLayer.videoGravity = .resizeAspect
        view.update(cameraID: cameraID)
        return view
    }

    func updateUIView(_ view: PreviewView, context: Context) {
        view.update(cameraID: cameraID)
    }
}

final class PreviewView: UIView {
    override class var layerClass: AnyClass { AVCaptureVideoPreviewLayer.self }

    var previewLayer: AVCaptureVideoPreviewLayer {
        // layerClass guarantees the type.
        layer as! AVCaptureVideoPreviewLayer // swiftlint:disable:this force_cast
    }

    private var coordinator: AVCaptureDevice.RotationCoordinator?
    private var observation: NSKeyValueObservation?
    private var currentID: String?

    func update(cameraID: String?) {
        guard let cameraID, cameraID != currentID, let device = AVCaptureDevice(uniqueID: cameraID) else { return }
        currentID = cameraID
        let coordinator = AVCaptureDevice.RotationCoordinator(device: device, previewLayer: previewLayer)
        self.coordinator = coordinator
        apply(coordinator.videoRotationAngleForHorizonLevelPreview)
        observation = coordinator.observe(\.videoRotationAngleForHorizonLevelPreview, options: [.new]) { [weak self] coordinator, _ in
            let angle = coordinator.videoRotationAngleForHorizonLevelPreview
            DispatchQueue.main.async {
                self?.apply(angle)
            }
        }
    }

    private func apply(_ angle: CGFloat) {
        if let connection = previewLayer.connection, connection.isVideoRotationAngleSupported(angle) {
            connection.videoRotationAngle = angle
        }
    }
}
