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

  # In-process llama.cpp server (provides polaris_llama_start/stop/is_running in
  # PolarisLlamaBridge.h). Build it into an xcframework from server.cpp + libllama
  # (arm64 device + simulator slices) and drop it here, then uncomment. Until
  # then the pod's start/stop won't link (the download/status surface is pure
  # Foundation). See README for the build recipe.
  # s.vendored_frameworks = 'ios/Frameworks/llama.xcframework'
end
