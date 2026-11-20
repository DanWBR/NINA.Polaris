require 'json'

package = JSON.parse(File.read(File.join(__dir__, 'package.json')))

Pod::Spec.new do |s|
  s.name = 'PolarisSmb'
  s.version = package['version']
  s.summary = package['description']
  s.license = package['license']
  s.homepage = 'https://github.com/DanWBR/NINA.Polaris'
  s.author = { 'Daniel Wagner' => 'danielwag@gmail.com' }
  s.source = { :git => 'https://github.com/DanWBR/NINA.Polaris.git', :tag => s.version.to_s }
  # The plugin plus SMBClient (MIT), vendored in ios/Plugin/SMBClient because
  # it ships only as a Swift package. See ios/Plugin/SMBClient/VENDORED.md.
  s.source_files = 'ios/Plugin/**/*.{swift,h,m}'
  s.ios.deployment_target = '15.0'
  s.dependency 'Capacitor'
  s.frameworks = 'Network', 'Security'
  # SMBClient uses parameter packs (Swift 5.9).
  s.swift_version = '5.9'
end
