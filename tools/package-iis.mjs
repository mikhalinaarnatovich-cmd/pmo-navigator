import fs from 'node:fs';
import path from 'node:path';
import {execFileSync} from 'node:child_process';
const [source,destination]=process.argv.slice(2).map(x=>path.resolve(x));
if(!source || !destination || !fs.existsSync(path.join(source,'pmo_nav.dll'))) throw new Error('Usage: node tools/package-iis.mjs <published-folder> <new-package-folder>');
if(fs.existsSync(destination)) throw new Error('Use a new, empty destination path.');
const app=path.join(destination,'app');
fs.cpSync(source,app,{recursive:true,filter:file=>{
  const relative=path.relative(source,file).replaceAll('\\','/');
  return relative!=='wwwroot/data' && !relative.startsWith('wwwroot/data/') && !/^appsettings.*\.json$/i.test(relative) && relative!=='web.config';
}});
fs.copyFileSync(path.join(source,'web.config'),path.join(app,'web.config.example'));
const migration=path.join(app,'Database','migrate_postgres.sql');
let sql=fs.readFileSync(migration,'utf8');
const seed=/INSERT INTO dbo\.(Employees|WorkCalendars)\s*\([\s\S]*?ON CONFLICT[^\n]*;/gi;
if([...sql.matchAll(seed)].length!==2) throw new Error('Expected exactly two seed blocks; inspect the migration before packaging.');
sql=sql.replace(seed,'-- Initial reference/test seeds omitted from the IIS deployment kit.');
fs.writeFileSync(migration,sql);
fs.writeFileSync(path.join(app,'appsettings.example.json'),JSON.stringify({
 ConnectionStrings:{PmoNavigatorDb:''},
 Logging:{LogLevel:{Default:'Information','Microsoft.AspNetCore':'Warning'}},
 AllowedHosts:'*',
 Feedback:{AdminLogins:['TP\\arnatovich_m'],AllowAnonymousSubmitOnRender:false},
 PeriodLocks:{Managers:['kot_a@tps.by','anufrienok_v@tps.by']},
 UbaLogins:['arnatovich_m','TP\\arnatovich_m'],
 ProjectsBasePath:'C:\\PmoNav\\projects',
 ProjectsDataPath:'C:\\PmoNav\\data',
 WorkCalendarPath:'C:\\PmoNav\\data\\КалендарьСПраздниками.csv'
},null,2)+'\n');
fs.copyFileSync(path.join(source,'Database','calendar-and-access.md'),path.join(destination,'Календарь-и-права.md'));
fs.copyFileSync('Database/iis-kit-readme.md',path.join(destination,'СНАЧАЛА-ПРОЧИТАТЬ.md'));
fs.writeFileSync(path.join(destination,'BUILD.txt'),'Source commit: '+execFileSync('git',['rev-parse','HEAD'],{encoding:'utf8'}).trim()+
 '\nFramework-dependent .NET 8 publish; Windows Hosting Bundle required.\nNo application settings, data CSV/JSON or initial reference seeds are included.\n');
console.log(destination);
