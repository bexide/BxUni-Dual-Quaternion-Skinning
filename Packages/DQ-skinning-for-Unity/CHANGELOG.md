# Changelog

## [0.0.7]

### Fixed
- [Metal](https://developer.apple.com/jp/metal/)で正常に動作しなかったのを修正

## [0.0.6]

### Fixed
- バウンディング情報が更新されず、正しくカリングが行えていなかったのを修正

## [0.0.5]

### Added
- `DualQuaternionSkinner`コンポーネントをオブジェクトに追加したとき、ComputeShaderへの参照を自動的に設定

### Fixed
- オリジナルのメッシュデータ(`SkinnedMeshRenderer`の`sharedMesh`)を破壊していたのを修正

## [0.0.4]

### Added
- URP対応シェーダー
- サンプルシーン

### Fixed
- エディタのPlayModeから抜けたときレンダラーの設定を元に戻すように

## [0.0.3]

### Fixed
- Modelインポーターのボーンの順序を整列する処理でボーンの正しい順序の判定ができていなかったのを修正

### Added
- 既存の`SkinnedMeshRenderer`が参照するボーンの順序がModelインポーターにより変更されたとき、
その変更を反映することができるエディタ拡張`RemapBoneUtility`を追加


## [0.0.2]

### Changed
- editorconfigをBeXide標準のものにして再びコード整形

## [0.0.1]

### Changed
- フォルダ構成をPackage向けに
- editorconfigに従ってコード整形

## [0.0.0]

fork from https://github.com/KosRud/DQ-skinning-for-Unity