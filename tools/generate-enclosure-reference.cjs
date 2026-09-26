// Usage: node tools/generate-enclosure-reference.cjs /path/to/extracted-official-rules.cjs output.json
// The input exports createState/applyMove/passTurn/calculateAreas/intersection from the
// public practice bundle. Keep the input separate: no JavaScript runtime is needed by tests.
const fs = require('node:fs');
const crypto = require('node:crypto');
const oracle = require(require('node:path').resolve(process.argv[2]));
const hash = value => crypto.createHash('sha256').update(value).digest('hex');
const same = (a,b) => a[0]===b[0] && a[1]===b[1];
const on = (p,s) => (p[0]-s.from[0])*(s.to[1]-s.from[1]) === (p[1]-s.from[1])*(s.to[0]-s.from[0]) &&
  p[0]>=Math.min(s.from[0],s.to[0]) && p[0]<=Math.max(s.from[0],s.to[0]) &&
  p[1]>=Math.min(s.from[1],s.to[1]) && p[1]<=Math.max(s.from[1],s.to[1]);
const interior = (p,s) => on(p,s) && !same(p,s.from) && !same(p,s.to);
const index=p=>p[0]*19+p[1];
const segmentKey=s=>[index(s.from),index(s.to)].sort((a,b)=>a-b).join(':');
const segments=[]; const ids=new Map();
for(let a=0;a<361;a++) for(let b=a+1;b<361;b++) {
 const from=[Math.floor(a/19),a%19],to=[Math.floor(b/19),b%19];
 if(Math.abs(from[0]-to[0])>3 || Math.abs(from[1]-to[1])>3)continue;
 ids.set(`${a}:${b}`,segments.length);segments.push({from,to});
}
function legal(state) {
 if(state.moveNumber>=120)return [];
 const own=state.turn,enemy=own==='blue'?'red':'blue';
 return segments.flatMap((s,id)=> {
  if(!state.nodes[own].some(p=>same(p,s.from)||same(p,s.to)))return [];
  if(state.nodes[own].some(p=>interior(p,s)))return [];
  if(state.segments[own].some(e=>oracle.intersection(s,e).kind==='collinear'||interior(s.from,e)||interior(s.to,e)))return [];
  const crossed=state.segments[enemy].filter(e=>oracle.intersection(s,e).kind!=='none');
  return crossed.length>1||crossed.some(e=>e.invincible)?[]:[id];
 });
}
const traces=[];
for(const seed of [17,731,9813]) {
 let state=oracle.createState(),rng=seed;const frames=[];
 const random=()=>{rng=(Math.imul(rng,1664525)+1013904223)>>>0;return rng/4294967296;};
 while(true) {
  let actions=legal(state); if(!actions.length&&state.moveNumber<120)actions=[7140];
  const record={moveNumber:state.moveNumber,turn:state.turn==='blue'?0:1,actionsRemaining:state.actionsRemaining,
   scores:[state.scores.blue,state.scores.red],areas:[state.areas.blue,state.areas.red],
   nodes:['blue','red'].map(c=>state.nodes[c].map(index).sort((a,b)=>a-b)),
   segments:['blue','red'].map(c=>state.segments[c].map(s=>({id:ids.get(segmentKey(s)),protected:s.invincible})).sort((a,b)=>a.id-b.id)),
   legalCount:actions.length,legalHash:hash(actions.join(',')),action:null};
  frames.push(record);if(state.moveNumber>=120)break;
  let candidates=actions;
  if(seed!==17&&random()<.8) {
   const enemy=state.turn==='blue'?'red':'blue';
   const tactical=actions.filter(id=>id!==7140&&(state.segments[enemy].some(e=>oracle.intersection(segments[id],e).kind!=='none') ||
     state.nodes[state.turn].some(p=>same(p,segments[id].from))&&state.nodes[state.turn].some(p=>same(p,segments[id].to))));
   if(tactical.length)candidates=tactical;
  }
  const action=candidates[Math.floor(random()*candidates.length)];record.action=action;
  if(action===7140)state=oracle.passTurn(state);
  else {const s=segments[action];const startOwned=state.nodes[state.turn].some(p=>same(p,s.from));state=oracle.applyMove(state,startOwned?s.from:s.to,startOwned?s.to:s.from);}
 }
 traces.push({seed,frames});
}
fs.writeFileSync(process.argv[3],JSON.stringify({source:'https://meaf.us/sst1/assets/index-BCoKy2Nw.js',
 referenceSha256:hash(fs.readFileSync(process.argv[2])),coordinateIndex:'19*x+y',traces}));
console.log(`Wrote ${traces.reduce((n,t)=>n+t.frames.length,0)} reference frames.`);
