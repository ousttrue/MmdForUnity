NM4U
====

NicoNicoカスタム版[MikuMikuDance for Unity](http://mmd-for-unity-proj.github.io/mmd-for-unity/)。

[PhotoStudioUnity5](http://github.o-in.dwango.co.jp/NicofarreDev/PhotoStudioUnity5)用に改造するMmdForUnity。

モーキャプでの使用を優先する。

# 変更点/予定
* [ ]出力ディレクトリを変更(pmd, pmxのあるフォルダ -> (pmd, pmx名).convert フォルダ)
* [x]Textureをconvertディレクトリにコピーする。ついでにRGBAのものにアルファチャンネルオプション
* [ ]揺れもの実装を追加する(なし, M4U original, unitychanのSpringJoint/SpringCollider, ニコニ立体方式)
* [ ]Pmdサポートを削除する(pmx変換一択にする)
* [ ]Material割り当てのカスタマイズ
* [ ]表情変更システム
* [ ]カメラ目線、自動瞬きシステム
* [ ]手ポーズシステム
* [ ]サンプルポーズ・モーションシステム

# 使い方
本家MmdForUnityと同じようにAssetsにコピーしてください。
AssetsBundleを作る側と、ロードする側両方を同じバージョンで更新するべし。

