// Generate explicit file links for Visual Studio Installer Projects. No API DLLs,
// machine-specific AI paths, per-user command registry or login profiles are shipped.
import fs from 'node:fs/promises';
import path from 'node:path';
import { createHash, randomUUID } from 'node:crypto';
const root = path.resolve(import.meta.dirname, '..');
const setup = import.meta.dirname;
const year = process.argv[2] || '2025';
if (!['2024', '2025', '2026'].includes(year)) throw new Error('Release scope is Revit 2024 through 2026.');
const version = JSON.parse(await fs.readFile(path.join(root, 'server/package.json'), 'utf8')).version;
const templatePath = path.join(setup, 'Setup.Template.vdproj');
try { await fs.access(templatePath); } catch { await fs.copyFile(path.join(setup, 'Sinotech_MCP_Setup.vdproj'), templatePath); }
const template = (await fs.readFile(templatePath, 'utf8')).replace(/^\uFEFF/, '').replaceAll('\r\n', '\n');
// The product prefix keeps every derived GUID (UpgradeCode, ProductCode, ...) distinct
// from TYBIM_MCP, whose generator hashes the same keys. A shared UpgradeCode would make
// one product's MSI uninstall the other as an "older version".
const uid = value => '_' + createHash('sha256').update('Sinotech_MCP:' + value).digest('hex').slice(0, 32).toUpperCase();
const guid = value => { const s = uid(value).slice(1); return '{' + [s.slice(0,8),s.slice(8,12),s.slice(12,16),s.slice(16,20),s.slice(20)].join('-') + '}'; };
const encode = value => value.replaceAll('\\', '\\\\').replaceAll('"', '');
const prop = (key, value, type = 8, indent = 8) => ' '.repeat(indent) + `"${key}" = "${type}:${encode(String(value))}"`;
function blockReplace(text, name, body, indent) {
  const spaces = ' '.repeat(indent);
  const regex = new RegExp(`^${spaces}"${name}"\\s*\\n${spaces}\\{[\\s\\S]*?^${spaces}\\}`, 'm');
  if (!regex.test(text)) throw new Error('Missing setup block ' + name);
  return text.replace(regex, spaces + '"' + name + '"\n' + spaces + '{\n' + body + '\n' + spaces + '}');
}
async function walk(dir) {
  const files = [];
  for (const entry of await fs.readdir(dir, { withFileTypes: true })) {
    if (entry.isSymbolicLink()) throw new Error('Payload cannot contain links: ' + entry.name);
    if (entry.isDirectory()) files.push(...await walk(path.join(dir, entry.name)));
    else files.push(path.join(dir, entry.name));
  }
  return files.sort();
}
const payload = path.join(setup, 'payloads', year);
const files = await walk(payload);
if (!files.some(f => path.basename(f) === 'RevitMCPPlugin.dll')) throw new Error('Prepare MSI payload before generating setup.');
for (const f of files) if (/^(RevitAPI.*\.dll|commandRegistry.json|auth.json)$/i.test(path.basename(f))) throw new Error('Forbidden payload file: ' + f);
const registration = path.join(setup, 'registration', year, 'Sinotech_MCP.addin');
await fs.mkdir(path.dirname(registration), { recursive: true });
await fs.writeFile(registration, `<?xml version="1.0" encoding="utf-8"?>\n<RevitAddIns><AddIn Type="Application"><Name>Sinotech_MCP</Name><Assembly>..\\Sinotech_MCP\\${year}\\RevitMCPPlugin.dll</Assembly><ClientId>A4D44EAC-098F-4A68-AD4E-1AD6DCF8CD26</ClientId><FullClassName>revit_mcp_plugin.Core.Application</FullClassName><VendorId>Sinotech</VendorId><VendorDescription>中興工程顧問股份有限公司 Sinotech Engineering Consultants</VendorDescription></AddIn></RevitAddIns>\n`);
const rootId = uid('application-root'), registrationId = uid('registration-' + year);
const folders = new Map([['', { id: rootId, children: new Map() }]]);
for (const file of files) {
  const relative = path.relative(payload, path.dirname(file)).replaceAll('\\', '/');
  let current = '', parent = folders.get('');
  for (const part of [year, ...relative.split('/').filter(Boolean)]) {
    current = current ? current + '/' + part : part;
    if (!folders.has(current)) { const child = { id: uid('folder-' + current), name: part, children: new Map() }; folders.set(current, child); parent.children.set(part, child); }
    parent = folders.get(current);
  }
}
function folderNode(folder, indent = 12, application = false, register = false) {
  const s = ' '.repeat(indent), kind = application || register ? '3C67513D-01DD-4637-8A68-80971EB9504F' : '9EF0B969-E518-4E46-987F-47570745A589';
  const lines = [s + `"{${kind}}:${folder.id}"`, s + '{'];
  if (application || register) lines.push(prop('DefaultLocation', '[AppDataFolder]Autodesk\\Revit\\Addins\\' + (register ? year : 'Sinotech_MCP'), 8, indent));
  lines.push(prop('Name', application ? '#1925' : register ? 'Revit ' + year + ' registration' : folder.name, 8, indent), prop('AlwaysCreate','FALSE',11,indent), prop('Condition','',8,indent), prop('Transitive','FALSE',11,indent), prop('Property', application ? 'TARGETDIR' : register ? 'SINOTECHREGISTERDIR' : folder.id + '_DIR',8,indent));
  lines.push(s + '    "Folders"',s + '    {', ...[...folder.children.values()].map(f => folderNode(f, indent + 8)),s + '    }',s + '}');
  return lines.join('\n');
}
function fileEntry(source, targetFolder, projectDir) {
  const id = uid('file-' + year + '-' + path.relative(setup,source));
  const lines = [`            "{1FB2D0AE-D3B9-43D4-B9DD-F88EC61E35DE}:${id}"`, '            {'];
  for (const [key, value, type = 8] of [['SourcePath',path.relative(projectDir,source)],['TargetName',path.basename(source)],['Tag',''],['Folder',targetFolder],['Condition',''],['Transitive','FALSE',11],['Vital','TRUE',11],['ReadOnly','FALSE',11],['Hidden','FALSE',11],['System','FALSE',11],['Permanent','FALSE',11],['SharedLegacy','FALSE',11],['PackageAs',1,3],['Register',1,3],['Exclude','FALSE',11],['IsDependency','FALSE',11],['IsolateTo','']]) lines.push(prop(key,value,type,12));
  lines.push('            }');
  return { id, text: lines.join('\n') };
}
for (const projectDir of [path.join(setup, 'generated', year), ...(year === '2025' ? [setup] : [])]) {
  await fs.mkdir(projectDir, { recursive: true });
  const projectName = projectDir === setup ? 'Sinotech_MCP_Setup' : 'Sinotech_MCP_' + year;
  let text = template;
  const entries = files.map(f => {
    const rel = path.relative(payload,path.dirname(f)).replaceAll('\\','/');
    return fileEntry(f, folders.get(year + (rel ? '/' + rel : '')).id, projectDir);
  });
  entries.push(fileEntry(registration, registrationId, projectDir));
  text = blockReplace(text,'File',entries.map(e => e.text).join('\n'),8);
  text = blockReplace(text,'Hierarchy',entries.map(e => `        "Entry"\n        {\n        "MsmKey" = "8:${e.id}"\n        "OwnerKey" = "8:_UNDEFINED"\n        "MsmSig" = "8:_UNDEFINED"\n        }`).join('\n'),4);
  text = blockReplace(text,'Folder',folderNode(folders.get(''),12,true) + '\n' + folderNode({ id:registrationId,children:new Map() },12,false,true),8);
  // Installation location is intentionally fixed. A relative .addin must not be
  // broken by moving TARGETDIR independently of the registration directory.
  const lines = text.split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    if (!/"SourcePath".*Vsd(?:Admin)?FolderDlg\.wid/.test(lines[i])) continue;
    const indent = lines[i].match(/^\s*/)[0];
    let start = i; while (start >= 0 && !new RegExp('^' + indent + '"\\{[^" ]+\\}:').test(lines[start])) start--;
    let end = i; while (end < lines.length && lines[end] !== indent + '}') end++;
    if (start < 0 || end >= lines.length) throw new Error('Cannot remove installation-folder dialog safely');
    lines.splice(start,end-start+1); i=start;
  }
  text = lines.join('\n');
  const replacements = { ProjectName:projectName,ProductName:'Sinotech_MCP for Revit ' + year,Title:'Sinotech_MCP for Revit ' + year,ProductVersion:version,ProductCode:guid('product-' + year + '-' + version),PackageCode:'{' + randomUUID().toUpperCase() + '}',UpgradeCode:guid('upgrade-' + year),RemovePreviousVersions:'TRUE',DetectNewerInstalledVersion:'TRUE',InstallAllUsers:'FALSE',InstallerBootstrapper:'2', ARPCOMMENTS:'Revit ' + year + ' AI assistant and MCP tools' };
  for (const [key,value] of Object.entries(replacements)) text = text.replace(new RegExp(`("${key}" = "[0-9]+:)[^"]*"`,'g'), '$1' + value + '"');
  const output = path.relative(projectDir,path.join(setup,'Release',`Sinotech_MCP_Revit${year}_${version}.msi`));
  text = text.replace(/("OutputFilename" = "8:)[^"]*"/g, '$1' + encode(output) + '"');
  const projectFile = path.join(projectDir,projectName + '.vdproj');
  // Visual Studio may memory-map the open setup project. Replacing a fully
  // written sibling file avoids truncating that mapped file during generation.
  const temporaryProject = projectFile + '.' + randomUUID() + '.tmp';
  try {
    await fs.writeFile(temporaryProject,'\uFEFF' + text.replaceAll('\n','\r\n'));
    await fs.rename(temporaryProject, projectFile);
  } finally { await fs.rm(temporaryProject, { force: true }); }
  if (projectDir !== setup) {
    const projectGuid = guid('setup-project-' + year);
    const solution = `Microsoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\nProject("{54435603-DBB4-11D2-8724-00A0C9A8B90C}") = "${projectName}", "${projectName}.vdproj", "${projectGuid}"\nEndProject\nGlobal\n GlobalSection(SolutionConfigurationPlatforms) = preSolution\n  Release|Any CPU = Release|Any CPU\n EndGlobalSection\n GlobalSection(ProjectConfigurationPlatforms) = postSolution\n  ${projectGuid}.Release|Any CPU.ActiveCfg = Release\n  ${projectGuid}.Release|Any CPU.Build.0 = Release\n EndGlobalSection\nEndGlobal\n`;
    await fs.writeFile(path.join(projectDir,projectName + '.sln'), '\uFEFF' + solution.replace(/^  /gm, '\t\t').replace(/^ /gm, '\t').replaceAll('\n', '\r\n'));
  }
  console.log(projectFile + ': ' + entries.length + ' explicit file links');
}
const index = await Promise.all(files.map(async f => ({relative:path.relative(payload,f).replaceAll('\\','/'),sha256:createHash('sha256').update(await fs.readFile(f)).digest('hex')})));
await fs.writeFile(path.join(setup,'generated',year,'payload-index.json'),JSON.stringify({year,version,applicationRoot:'[AppDataFolder]Autodesk\\Revit\\Addins\\Sinotech_MCP',manifest:'[AppDataFolder]Autodesk\\Revit\\Addins\\' + year + '\\Sinotech_MCP.addin',assembly:'..\\Sinotech_MCP\\' + year + '\\RevitMCPPlugin.dll',files:index},null,2));


