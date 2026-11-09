import fs from 'node:fs';
// Synthetic, deliberately NOT a real production calendar.
const lines = ['ДатаКалендаряСПраздниками|СтандартныеЧасыРаботы'];
for (const month of ['2026-10','2026-11','2026-12','2027-01']) {
  const [year,m] = month.split('-').map(Number);
  for(let day=1; day<=new Date(Date.UTC(year,m,0)).getUTCDate(); day++) {
    let hours=day<=20?8:0;
    if(month==='2026-11' && day===1) hours=7;
    if(month==='2026-11' && day===2) hours=0;
    lines.push(`${month}-${String(day).padStart(2,'0')}|${hours}`);
  }
}
fs.writeFileSync('/tmp/pmo-calendar-qa.csv', lines.join('\n')+'\n');
console.log('Synthetic calendar created outside project/webroot.');
