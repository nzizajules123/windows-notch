package com.windowsnotch.island

import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import androidx.core.content.FileProvider
import java.io.File
import java.io.OutputStream
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream

/**
 * Saves what the island wrote for you (a letter, notes, code…) into the phone's shared storage, where your file
 * manager and other apps can see it: Documents/Notch by default, or Download, Documents/Letters… Word files (.docx) are
 * built directly; everything else is plain text. Programs (.apk) are never written.
 */
object FilesOut {
    private val blocked = setOf("apk", "aab", "dex", "so", "sh", "jar")

    /** Returns (where it was saved, a uri to open it). */
    fun save(ctx: Context, content: String, fileName: String, folder: String, format: String): Pair<String, Uri> {
        require(content.isNotBlank()) { "There was nothing to write." }
        var name = fileName.filter { it !in "\\/:*?\"<>|" }.trim().trimEnd('.').ifBlank { "Notch note" }
        var ext = name.substringAfterLast('.', "").lowercase()
        if (ext.isEmpty() || ext.length > 5) { ext = format.trim().trimStart('.').lowercase().ifBlank { "docx" }; name += ".$ext" }
        require(ext !in blocked) { "I don't create .$ext files." }

        // "Documents\Letters" → Documents/Letters; "downloads" → Download; nothing → Documents/Notch
        val f = folder.replace('\\', '/').trim('/', ' ').replace(Regex("^(my|the)\\s+", RegexOption.IGNORE_CASE), "")
        val first = f.substringBefore('/').lowercase()
        val rest = f.substringAfter('/', "")
        val base = when (first) {
            "download", "downloads" -> Environment.DIRECTORY_DOWNLOADS
            "documents", "docs", "my documents", "" -> Environment.DIRECTORY_DOCUMENTS
            "pictures", "photos" -> Environment.DIRECTORY_PICTURES
            "music" -> Environment.DIRECTORY_MUSIC
            else -> Environment.DIRECTORY_DOCUMENTS
        }
        val sub = when {
            first.isEmpty() -> "Notch"
            first in setOf("download", "downloads", "documents", "docs", "my documents", "pictures", "photos", "music") -> rest
            else -> f   // a plain name: a folder of that name in Documents
        }
        val rel = if (sub.isBlank()) base else "$base/$sub"
        val mime = if (ext == "docx") "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                   else MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "text/plain"

        val uri: Uri
        if (Build.VERSION.SDK_INT >= 29) {
            val values = ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, name)
                put(MediaStore.MediaColumns.MIME_TYPE, mime)
                put(MediaStore.MediaColumns.RELATIVE_PATH, "$rel/")
            }
            val collection = if (base == Environment.DIRECTORY_DOWNLOADS) MediaStore.Downloads.EXTERNAL_CONTENT_URI else MediaStore.Files.getContentUri("external")
            uri = ctx.contentResolver.insert(collection, values) ?: error("Couldn't create the file.")
            ctx.contentResolver.openOutputStream(uri)!!.use { write(it, content, ext) }
        } else {
            val dir = File(ctx.getExternalFilesDir(null), rel).apply { mkdirs() }
            var file = File(dir, name)
            var i = 2
            while (file.exists()) { file = File(dir, name.substringBeforeLast('.') + " ($i)." + ext); i++ }
            file.outputStream().use { write(it, content, ext) }
            uri = FileProvider.getUriForFile(ctx, "${ctx.packageName}.files", file)
        }
        return "$rel/$name" to uri
    }

    fun open(ctx: Context, uri: Uri, mime: String? = null) {
        val type = mime ?: ctx.contentResolver.getType(uri) ?: "*/*"
        val i = Intent(Intent.ACTION_VIEW).setDataAndType(uri, type)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        runCatching { (IslandService.instance ?: ctx).startActivity(i) }
    }

    private fun write(out: OutputStream, content: String, ext: String) {
        if (ext != "docx") { out.write(content.replace("\r\n", "\n").toByteArray(Charsets.UTF_8)); return }
        val body = StringBuilder()
        for (raw in content.replace("\r\n", "\n").split('\n')) {
            var line = raw.trimEnd()
            var heading = 0
            Regex("^(#{1,3})\\s+(.*)$").find(line)?.let { heading = it.groupValues[1].length; line = it.groupValues[2] }
            Regex("^\\s*[-*•]\\s+(.*)$").find(line)?.let { line = "•  " + it.groupValues[1] }
            val size = when (heading) { 1 -> 36; 2 -> 30; 3 -> 26; else -> 0 }
            body.append("<w:p><w:pPr><w:spacing w:after=\"${if (heading > 0) 160 else 120}\"/></w:pPr>")
            for (part in splitBold(line)) {
                val bold = heading > 0 || part.second
                body.append("<w:r><w:rPr>").append(if (bold) "<w:b/>" else "").append(if (size > 0) "<w:sz w:val=\"$size\"/>" else "")
                    .append("</w:rPr><w:t xml:space=\"preserve\">").append(esc(part.first)).append("</w:t></w:r>")
            }
            body.append("</w:p>")
        }
        ZipOutputStream(out).use { z ->
            fun add(n: String, s: String) { z.putNextEntry(ZipEntry(n)); z.write(s.toByteArray(Charsets.UTF_8)); z.closeEntry() }
            add("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>")
            add("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>")
            add("word/document.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>$body<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>")
        }
    }

    /** "Hello **world**" → [("Hello ", false), ("world", true)] */
    private fun splitBold(line: String): List<Pair<String, Boolean>> {
        val out = ArrayList<Pair<String, Boolean>>()
        var i = 0
        Regex("\\*\\*([^*]+)\\*\\*").findAll(line).forEach { m ->
            if (m.range.first > i) out.add(line.substring(i, m.range.first) to false)
            out.add(m.groupValues[1] to true)
            i = m.range.last + 1
        }
        if (i < line.length) out.add(line.substring(i) to false)
        if (out.isEmpty()) out.add("" to false)
        return out
    }

    private fun esc(s: String) = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;")
}
