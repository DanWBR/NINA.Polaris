require 'json'

package = JSON.parse(File.read(File.join(__dir__, 'package.json')))

Pod::Spec.new do |s|
  s.name = 'PolarisLlama'
  s.version = package['version']
  s.summary = package['description']
  s.license = package['license']
  s.homepage = 'https://github.com/DanWBR/NINA.Polaris'
  s.author = { 'Daniel Wagner' => 'danielwag@gmail.com' }
  s.source = { :git => 'https://github.com/DanWBR/NINA.Polaris.git', :tag => s.version.to_s }
  s.source_files = 'ios/Plugin/**/*.{swift,h,m}'
  s.ios.deployment_target = '14.0'
  s.dependency 'Capacitor'
  s.swift_version = '5.1'
  # iOS in-process llama.cpp embed (xcframework) is deferred; the stub only
  # reports unavailable for now, so no extra pod dependency yet.
end
