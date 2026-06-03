require 'json'

package = JSON.parse(File.read(File.join(__dir__, 'package.json')))

Pod::Spec.new do |s|
  s.name = 'PolarisOnnx'
  s.version = package['version']
  s.summary = package['description']
  s.license = package['license']
  s.homepage = 'https://github.com/DanWBR/NINA.Polaris'
  s.author = 'DanWBR'
  s.source = { :git => 'https://github.com/DanWBR/NINA.Polaris.git', :tag => s.version.to_s }
  s.source_files = 'ios/Plugin/**/*.{swift,h,m}'
  s.ios.deployment_target = '14.0'
  s.dependency 'Capacitor'
  # ONNX Runtime for iOS with the CoreML execution provider.
  s.dependency 'onnxruntime-objc', '~> 1.18.0'
  s.swift_version = '5.1'
end
