import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
const root = path.resolve('third_party');
await fs.mkdir(root, {recursive:true});
const files = [
  ['wxemoticon-v0.3.0.zip', 'https://github.com/liusheng22/export-wechat-emoji/releases/download/v0.3.0/wxemoticon-x86_64-windows.zip', 'ceb10c0b2052716cc493e5446d3cb59d1b3090d2b64014ad6ddc7b3b28a033d0'],
  ['wxemoticon-v0.3.0-source.zip', 'https://codeload.github.com/liusheng22/export-wechat-emoji/zip/refs/tags/v0.3.0', null]
];
for (const [name,url,expected] of files) {
  const response = await fetch(url,{headers:{'User-Agent':'EmojiBridge'}});
  if (!response.ok) throw new Error('HTTP '+response.status+' '+name);
  const bytes=Buffer.from(await response.arrayBuffer());
  const hash=crypto.createHash('sha256').update(bytes).digest('hex');
  if(expected && hash!==expected) throw new Error('SHA256 mismatch: '+name);
  await fs.writeFile(path.join(root,name),bytes);
  console.log(name+' '+bytes.length+' bytes SHA256 '+hash);
}
