'use strict';
if(navigator.userAgent.includes('CodexLanConsole/'))document.documentElement.classList.add('native-shell');
const $ = s => document.querySelector(s);
const themeColor = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const names = {walk:'步行',bus:'公交',bike:'自行车',scooter:'Scooter',car:'开车'};
const paths = {
  walk:'M13 5a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM7 22l3-7-2-4 3-5 4 1 2 5 4 1M8 9l-4 4M12 14l4 3 1 5',
  bus:'M5 17h14V6c0-3-14-3-14 0v11ZM5 10h14M8 18v3M16 18v3M8 14h1M15 14h1',
  bike:'M9 16a4 4 0 1 1-8 0 4 4 0 0 1 8 0ZM23 16a4 4 0 1 1-8 0 4 4 0 0 1 8 0ZM5 16l5-9 6 9H5ZM10 7H7M16 16l-1-12h3',
  scooter:'M7 19a2 2 0 1 1-4 0 2 2 0 0 1 4 0ZM22 19a2 2 0 1 1-4 0 2 2 0 0 1 4 0ZM7 18h7l4-7M20 17 16 3h-4',
  car:'M3 17V10l3-6h12l3 6v7H3ZM3 10h18M6 17v3M18 17v3M6 14h2M16 14h2'};
const icon = mode => `<span class="mode-icon"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="${paths[mode] || paths.bus}"/></svg></span>`;
let state, plan, live, selected, direction='toCampus', busy=false, draft, map, routeLayer, vehicleLayer, stopLayer;
let liveStop, liveRoute, hasFit=false, requestRevision=0, liveRevision=0, autoTimer;
let refreshPending=false;
const fmtTime = value => new Date(value).toLocaleTimeString('zh-CN',{timeZone:'America/Los_Angeles',hour:'2-digit',minute:'2-digit',hour12:false});
const fmtDate = value => new Date(value).toLocaleDateString('zh-CN',{timeZone:'America/Los_Angeles',month:'long',day:'numeric',weekday:'long'});
const mins = n => Math.max(0, Math.round(n || 0));
const meters = n => n>=1000?`${(n/1000).toFixed(1)} km`:`${Math.round(n)} m`;
function localInput(date=new Date()) {
  const p=Object.fromEntries(new Intl.DateTimeFormat('en-CA',{timeZone:'America/Los_Angeles',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',hourCycle:'h23'}).formatToParts(date).map(x=>[x.type,x.value]));
  return `${p.year}-${p.month}-${p.day}T${p.hour}:${p.minute}`;
}
function toast(text){$('#toast').textContent=text;$('#toast').classList.add('show');clearTimeout(toast.timer);toast.timer=setTimeout(()=>$('#toast').classList.remove('show'),4500);}
function notice(text){$('#notice').textContent=text;$('#notice').hidden=!text;}
async function api(path, method='GET', body) {
  const headers={};const token=localStorage.getItem('codexLanToken');if(token)headers.Authorization=`Bearer ${token}`;
  if(body!==undefined)headers['Content-Type']='application/json';
  const controller=new AbortController();const timeout=setTimeout(()=>controller.abort(),55000);
  try{
    const r=await fetch('/api'+path,{method,headers,body:body===undefined?undefined:JSON.stringify(body),credentials:'same-origin',cache:'no-store',signal:controller.signal});
    const j=await r.json();
    if(r.status===401){if(!$('#pairDialog').open)$('#pairDialog').showModal();throw new Error('请先连接你的 Codex Console。');}
    if(!r.ok){const error=new Error(j.error||`请求暂未完成 (${r.status})`);error.status=r.status;throw error;}
    return j;
  }finally{clearTimeout(timeout);}
}
function mapInit(){
  if(!window.L){$('#map').innerHTML='<p class="empty">地图暂未加载，文字路线仍可使用。</p>';return;}
  map=L.map('map',{scrollWheelZoom:false}).setView([32.878,-117.229],14);
  L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png',{maxZoom:19,attribution:'© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'}).addTo(map);
  routeLayer=L.layerGroup().addTo(map);vehicleLayer=L.layerGroup().addTo(map);stopLayer=L.layerGroup().addTo(map);
}
function decodePolyline(s){
  const points=[];let index=0,lat=0,lon=0;
  while(index<s.length){let coords=[];for(let part=0;part<2;part++){let shift=0,result=0,b;do{if(index>=s.length||shift>30)return points;b=s.charCodeAt(index++)-63;result|=(b&31)<<shift;shift+=5;}while(b>=32);coords.push(result&1?~(result>>1):result>>1);}lat+=coords[0];lon+=coords[1];points.push([lat/1e5,lon/1e5]);}return points;
}
function drawRoute(fit=false){
  if(!map||!plan)return;routeLayer.clearLayers();const points=[];
  if(selected)for(const leg of selected.legs){const line=decodePolyline(leg.geometry||'');if(line.length){points.push(...line);L.polyline(line,{color:themeColor(leg.mode==='WALK'?'--muted':'--green'),weight:5,opacity:.9,dashArray:leg.mode==='WALK'?'5 7':undefined}).addTo(routeLayer);}}
  for(const [place,end] of [[plan.from,false],[plan.to,true]]){const xy=[place.lat,place.lon];points.push(xy);L.marker(xy,{icon:L.divIcon({className:`point-marker ${end?'end':''}`,iconSize:[17,17]})}).bindPopup(esc(place.name)).addTo(routeLayer);}
  if((fit||!hasFit)&&points.length){map.fitBounds(L.latLngBounds(points),{padding:[28,28],maxZoom:16});hasFit=true;}
  $('#mapStatus').textContent=selected?meters(selected.distanceMeters):'UCSD';
}
function drawVehicles(){
  if(!map||!live)return;vehicleLayer.clearLayers();stopLayer.clearLayers();
  for(const stop of live.stops||[])L.circleMarker([stop.lat,stop.lon],{radius:4,color:themeColor('--muted'),weight:1,fillOpacity:1,fillColor:themeColor('--field')}).bindPopup(esc(stop.name)).addTo(stopLayer);
  for(const v of live.vehicles||[])if(Date.now()-v.updatedAt<180000)L.marker([v.lat,v.lon],{icon:L.divIcon({className:'bus-marker',html:'BUS',iconSize:[32,32]})}).bindPopup(`公交位置 · ${esc(fmtTime(v.updatedAt))}`).addTo(vehicleLayer);
}
function renderState(){
  if(!state)return;const s=state.settings;
  $('#originName').textContent=direction==='toCampus'?s.home.name:s.campus.name;
  $('#destinationName').textContent=direction==='toCampus'?s.campus.name:s.home.name;
  $('#reminderTitle').textContent=s.remindersEnabled?'每天的出发提醒已安排':'出发提醒尚未开启';
  $('#reminderSummary').textContent=s.remindersEnabled?`${s.morningArrival} 前到校 · ${s.eveningArrival} 前到家，出发前 ${s.remindMinutes} 分钟提醒。需 Android 后台通知在线。`:'先设定你平时的到达时间，再开启提醒。';
  $('#vehicles').innerHTML=['bike','scooter','car'].map(mode=>`<label class="vehicle-row"><span>${names[mode]}</span><select data-vehicle="${mode}" aria-label="${names[mode]}当前位置"><option value="unavailable">尚未拥有 / 不可用</option><option value="home">在住处</option><option value="campus">在 HDSI</option></select></label>`).join('');
  for(const el of document.querySelectorAll('[data-vehicle]'))el.value=s.vehicles[el.dataset.vehicle]||'unavailable';
  const active=state.activeTrip;$('#activeTrip').hidden=!active;
  if(active)$('#activeTrip').innerHTML=`<div><strong>${esc(names[active.mode])}行程进行中</strong><p>${active.direction==='toCampus'?'前往 HDSI':'返回住处'} · ${fmtTime(active.startedAt)} 出发</p></div><div><button class="secondary" id="cancelTrip">取消记录</button><button class="primary" id="finishTrip">已到达 ✓</button></div>`;
  $('#history').innerHTML=state.history.length?state.history.map(t=>`<div class="history-row">${icon(t.mode)}<div><b>${esc(names[t.mode])} · ${t.direction==='toCampus'?'去 HDSI':'回家'}</b><small>${esc(fmtDate(t.startedAt))} ${fmtTime(t.startedAt)} → ${fmtTime(t.finishedAt)}</small><small>预估 ${mins(t.expectedMinutes)} 分钟 · ${meters(t.distanceMeters)}</small></div><strong>${mins((new Date(t.finishedAt)-new Date(t.startedAt))/60000)}<small>分钟</small></strong></div>`).join(''):'<p class="empty">还没有行程。出发时点一下“开始这段行程”，到达后确认，就能逐步校准你的通勤时间。</p>';
}
function renderPlan(){
  if(!plan)return;
  const best=plan.options.find(x=>x.id===plan.recommendedId);
  if(best){
    const departIn=Math.round((new Date(best.leaveAt)-Date.now())/60000);
    $('#recommendation').innerHTML=`<div class="hero-top"><span class="eyebrow">${plan.arriveBy?'按到达目标推荐':'现在出发 · 门到门比较'}</span><span class="hero-badge">${esc(best.basis)}</span></div><h2>${esc(best.title)}<span> ↗</span></h2><div class="hero-result"><strong>${mins(best.minutes)}<span>分钟</span></strong><div class="hero-time"><b>${fmtTime(best.leaveAt)} 出发</b>${fmtTime(best.arriveAt)} 预计到达${plan.arriveBy?` · 目标 ${fmtTime(plan.requestedTime)}`:''}</div></div><p>${best.mode==='bus'?`步行 ${mins(best.walkMinutes)} 分钟 · 等车 ${mins(best.waitMinutes)} 分钟 · ${meters(best.distanceMeters)}`:`全程 ${meters(best.distanceMeters)} · ${best.samples>=3?'已参考你的实际行程':'可在设置中调整个人速度'}`}</p><div class="hero-footer"><span>${departIn>1?`距离建议出发还有 ${departIn} 分钟`:'准备好就可以出发'}${plan.arriveBy?` · 留出 ${state.settings.bufferMinutes} 分钟余量`:''}</span><button id="startRecommended" ${state.activeTrip?'disabled':''}>${state.activeTrip?'行程进行中':'开始这段行程'} →</button></div>`;
  }else $('#recommendation').innerHTML='<p class="eyebrow">暂未找到可用方案</p><h2>先别急，换个时间看看。</h2><p>请检查交通工具的位置，或切换“现在出发”。如果路线服务暂时离线，可以打开官方地图核对。</p><div class="hero-footer"><a style="color:white" href="https://wayfinder.ucsd.onebusawaycloud.com/" target="_blank" rel="noreferrer">打开官方路线服务 ↗</a></div>';
  $('#options').innerHTML=plan.options.map((o,i)=>`<button class="option ${o.id===selected?.id?'selected':''} ${!o.available?'unavailable':''}" data-option="${i}" aria-pressed="${o.id===selected?.id}">${icon(o.mode)}<span class="option-info"><b>${esc(o.title)}</b>${o.id===best?.id?'<span class="mini-tag">推荐</span>':''}<small>${o.available?`${fmtTime(o.leaveAt)} 出发 · ${fmtTime(o.arriveAt)} 到达`:'不在出发地 · 仅供比较'}<br>${esc(o.basis)}${o.mode==='bus'?` · 走 ${mins(o.walkMinutes)} 分 / 等 ${mins(o.waitMinutes)} 分`:''}</small></span><span class="option-time">${mins(o.minutes)} <span>分</span></span></button>`).join('')||'<p class="empty">没有可展示的完整路线，请换个时间重试。</p>';
  const missing=Object.keys(names).filter(m=>!plan.options.some(o=>o.mode===m));
  if(missing.length)$('#options').insertAdjacentHTML('beforeend',`<p class="note">${missing.map(m=>names[m]).join('、')}：本次没有返回可用路线。</p>`);
  $('#updated').textContent=`${fmtTime(plan.updatedAt)} 更新`;
  notice(plan.warnings.join(' '));renderDetails();drawRoute();
}
function renderDetails(){
  if(!selected)return;$('#routeTitle').textContent=`${selected.title} · 详细路线`;
  const travel=selected.mode==='bus'?'transit':selected.mode==='car'?'driving':selected.mode==='walk'?'walking':'bicycling';
  $('#directionsLink').href=`https://www.google.com/maps/dir/?api=1&origin=${plan.from.lat},${plan.from.lon}&destination=${plan.to.lat},${plan.to.lon}&travelmode=${travel}`;
  $('#routeSteps').innerHTML=selected.legs.map((l,i)=>`<div class="leg"><span class="leg-time">${fmtTime(l.startTime)}</span><div class="leg-body"><b>${l.route?`<span class="route-tag">${esc(l.route)}</span>`:''}${l.mode==='WALK'?'步行':l.mode==='BICYCLE'?esc(names[selected.mode]||'骑行'):l.mode==='CAR'?'驾驶':'乘车'} ${mins(l.minutes)} 分钟</b><p>${esc(i===0?plan.from.name:l.from)} → ${esc(i===selected.legs.length-1?plan.to.name:l.to)}</p><p>${l.route?`${l.realTime?'实时预测':'计划班次'} · ${fmtTime(l.endTime)} 下车`:esc(l.streets.filter(x=>!['path','sidewalk','steps','service road'].includes(x)).slice(0,4).join(' → '))}</p></div></div>`).join('')+`<div class="leg"><span class="leg-time">${fmtTime(selected.arriveAt)}</span><div class="leg-body"><b>到达 ${esc(plan.to.name)}</b><p>${selected.available?'':'这个工具当前不可用，请先确认车辆位置。'}</p></div></div>`;
  $('#routeNotes').innerHTML=selected.notes.map(n=>`<p class="note">· ${esc(n)}</p>`).join('')+`<button class="secondary" id="startSelected" ${!selected.available||state.activeTrip?'disabled':''}>使用这个方案出发</button>`;
}
async function refreshPlan(manual=false){
  if(!state)return;if(busy){refreshPending=true;return;}busy=true;const rev=++requestRevision;const requestedDirection=direction;$('#refreshPlan').disabled=true;$('#refreshPlan').textContent='计算中…';
  try{
    const mode=$('#timeMode').value;const before=selected?{mode:selected.mode,title:selected.title}:null;
    const next=await api('/commute/plan','POST',{direction,arriveBy:mode==='arrive',when:mode==='now'?null:$('#when').value});
    if(rev!==requestRevision||requestedDirection!==direction){refreshPending=true;return;}plan=next;selected=(before&&plan.options.find(o=>o.mode===before.mode&&o.title===before.title))||plan.options.find(o=>o.id===plan.recommendedId)||plan.options[0];
    renderPlan();const bus=(selected?.mode==='bus'?selected:plan.options.find(o=>o.mode==='bus'))?.legs.find(l=>l.stopId&&l.routeId);
    if(bus){liveStop=bus.stopId;liveRoute=bus.routeId;}else{liveStop=direction==='toCampus'?'UCSD_9912':'UCSD_10772';liveRoute='UCSD_1050';}
    await refreshLive();
  }catch(e){notice(e.name==='AbortError'?'路线服务响应较慢，请稍后重试。':e.message);if(!plan)$('#recommendation').innerHTML='<p class="eyebrow">还未取得路线</p><h2>请连接电脑后重试。</h2><p>这里不会用演示时间代替真实通勤方案。</p>';else $('#updated').textContent='更新失败 · 上次结果，仅供参考';}
  finally{busy=false;$('#refreshPlan').disabled=false;$('#refreshPlan').textContent='比较路线 ↗';if(refreshPending){refreshPending=false;refreshPlan();}}
}
async function refreshLive(){
  const rev=++liveRevision;
  try{const next=await api(`/commute/live?direction=${direction}&stopId=${encodeURIComponent(liveStop||'UCSD_9912')}&routeId=${encodeURIComponent(liveRoute||'UCSD_1050')}`);if(rev!==liveRevision)return;live=next;renderLive();}
  catch(e){$('#liveStatus').textContent='连接中断';$('#departures').innerHTML='<p class="empty">实时信息暂不可用，请打开官方查询。</p>';vehicleLayer?.clearLayers();}
}
function renderLive(){
  if(!live)return;$('#stopSelect').innerHTML=live.stops.map(s=>`<option value="${esc(s.id)}">${esc(s.name)}</option>`).join('');$('#stopSelect').value=live.stopId;
  const visible=live.departures.filter(d=>d.departureTime>Date.now()-30000);
  $('#departures').innerHTML=visible.map(d=>`<div class="departure"><span class="line-label">${esc(d.route)}</span><span class="destination">${esc(d.destination||'公交')}<small>${fmtTime(d.departureTime)} · ${d.realtime?'实时预测':'计划时刻'}</small></span><strong>${Math.max(0,Math.ceil((d.departureTime-Date.now())/60000))}<small> 分</small></strong></div>`).join('')||'<p class="empty">未来 90 分钟没有可上车班次。请核对运营时间和服务公告。</p>';
  $('#liveStatus').textContent=live.errors.length?'部分数据不可用':visible.some(d=>d.realtime)?'● 实时预测':'时刻表 / 暂无预测';
  $('#serviceNote').textContent=[live.serviceNote,...live.errors].join(' ');drawVehicles();
}
async function startTrip(option){
  if(!option||!option.available)return;
  if(Date.now()-new Date(plan.updatedAt)>90000){toast('先刷新一下路线，避免使用过期班次。');refreshPlan(true);return;}
  if(new Date(option.leaveAt)-Date.now()>5*60000){toast('还没到出发时间。真正出发时再开始记录，才能准确学习速度。');return;}
  try{state=await api('/commute/trips/start','POST',{direction,mode:option.mode,distanceMeters:option.distanceMeters,expectedMinutes:option.minutes});renderState();renderPlan();toast('已开始记录。到达后点“已到达”。');}catch(e){toast(e.message);}
}
async function finishTrip(cancel){try{state=await api('/commute/trips/finish','POST',{id:state.activeTrip.id,cancel});renderState();await refreshPlan();toast(cancel?'已取消，不计入历史。':'已保存行程，车辆位置已同步。');}catch(e){toast(e.message);}}
function setupDialog(){
  draft=JSON.parse(JSON.stringify(state.settings));draft.revision=state.revision;
  $('#homeInput').value=draft.home.name;$('#campusInput').value=draft.campus.name;
  for(const k of ['morningArrival','eveningArrival','remindMinutes','bufferMinutes','walkKph','bikeKph','scooterKph','parkingMinutes'])$('#'+k).value=draft[k];
  $('#remindersEnabled').checked=draft.remindersEnabled;
  $('#weekdays').innerHTML=['日','一','二','三','四','五','六'].map((d,i)=>`<label><input type="checkbox" value="${i}" ${draft.days.includes(i)?'checked':''}><span>${d}</span></label>`).join('');
  $('#preferences').innerHTML=Object.entries(names).map(([k,v])=>`<label><input type="checkbox" value="${k}" ${draft.preferred.includes(k)?'checked':''}>${v}</label>`).join('');
  showCoordinates();$('#settingsError').textContent='';nativeStatus();
}
function showCoordinates(){for(const k of ['home','campus'])$('#'+k+'Coordinates').textContent=`${draft[k].lat.toFixed(5)}, ${draft[k].lon.toFixed(5)}`;}
async function findPlace(key){
  const button=key==='home'?$('#findHome'):$('#findCampus');button.disabled=true;
  try{const query=$('#'+key+'Input').value;const place=await api('/commute/location?query='+encodeURIComponent(query));draft[key]={...place,name:query};showCoordinates();toast('已找到坐标，请核对地点名称后保存。');}
  catch(e){$('#settingsError').textContent=e.message;}finally{button.disabled=false;}
}
async function saveSettings(e){
  e.preventDefault();$('#saveSettings').disabled=true;$('#settingsError').textContent='';
  try{
    if($('#homeInput').value!==draft.home.name||$('#campusInput').value!==draft.campus.name)throw new Error('地点名称已修改，请先点“查找坐标”再保存。');
    const preferred=[...document.querySelectorAll('#preferences input:checked')].map(x=>x.value);if(preferred.length>2)throw new Error('最多选择两种偏好。');
    for(const k of ['morningArrival','eveningArrival'])draft[k]=$('#'+k).value;
    for(const k of ['remindMinutes','bufferMinutes','walkKph','bikeKph','scooterKph','parkingMinutes'])draft[k]=Number($('#'+k).value);
    draft.days=[...document.querySelectorAll('#weekdays input:checked')].map(x=>+x.value);draft.remindersEnabled=$('#remindersEnabled').checked;draft.preferred=preferred;
    if(draft.remindersEnabled&&!draft.days.length)throw new Error('开启提醒后请至少选择一天。');
    const revision=draft.revision;delete draft.revision;state=await api('/commute/settings','PUT',{revision,settings:draft});$('#settingsDialog').close();renderState();hasFit=false;await refreshPlan(true);toast('已保存，手机和电脑使用同一份设置。');
  }catch(e){$('#settingsError').textContent=e.message;if(draft&&draft.revision===undefined)draft.revision=state.revision;}finally{$('#saveSettings').disabled=false;}
}
function nativeStatus(){
  const bridge=window.CodexAndroidNotifications;
  try{const v=bridge?.getStatus();const s=typeof v==='string'?JSON.parse(v):v;$('#notificationState').textContent=s?(s.enabled&&s.permission==='granted'?'手机通知已开启；请确认系统允许后台运行。':'手机后台通知尚未完全开启。'):'你现在在普通网页中。后台响铃请用已有 Codex Android APP 打开通勤助手；网页本身关闭后不能可靠提醒。';}
  catch{$('#notificationState').textContent='请返回 Console 设置检查后台通知状态。';}
}
function enableNotifications(){
  const b=window.CodexAndroidNotifications;if(!b){toast('请在 Codex Android APP 中打开此页面，再开启后台通知。');return;}
  try{const token=localStorage.getItem('codexLanToken');if(token)b.configure(token);b.setEnabled(true);const s=JSON.parse(b.getStatus());if(s.permission!=='granted')b.requestPermission();setTimeout(nativeStatus,800);}catch{toast('请在 Console 设置中开启后台通知。');}
}
async function load(){
  try{const result=await api('/commute/state');state=result.state;direction=state.location==='campus'?'toHome':'toCampus';setDirection(direction,false);renderState();
    const url=new URL(location.href);if(url.searchParams.get('panel')==='settings'){setupDialog();$('#settingsDialog').showModal();url.searchParams.delete('panel');history.replaceState(history.state,'',url.pathname+url.search);}
    await refreshPlan();if(result.loadWarning)notice(result.loadWarning);scheduleRefresh();}
  catch(e){notice(e.message);}
}
function setDirection(value,refresh=true){
  direction=value;for(const k of ['toCampus','toHome'])$('#'+k).classList.toggle('selected',k===value);selected=null;hasFit=false;renderState();
  if($('#timeMode').value!=='now')$('#when').value=localInput(new Date(Date.now()+3600000)).slice(0,11)+(value==='toCampus'?state.settings.morningArrival:state.settings.eveningArrival);
  if(refresh)refreshPlan(true);
}
function scheduleRefresh(){clearInterval(autoTimer);autoTimer=setInterval(async()=>{if(document.hidden||document.querySelector('dialog[open]')||busy)return;await refreshPlan();},60000);}
document.addEventListener('click',e=>{
  const open=e.target.closest('[data-open]');if(open){if(!state){toast('请先连接电脑。');return;}if(open.dataset.open==='settingsDialog')setupDialog();$('#'+open.dataset.open).showModal();}
  if(e.target.closest('[data-close]'))e.target.closest('dialog').close();
  const option=e.target.closest('[data-option]');if(option){selected=plan.options[+option.dataset.option];renderPlan();drawRoute(true);const leg=selected.legs.find(x=>x.stopId&&x.routeId);if(leg){liveStop=leg.stopId;liveRoute=leg.routeId;refreshLive();}}
  if(e.target.closest('#startRecommended'))startTrip(plan.options.find(o=>o.id===plan.recommendedId));
  if(e.target.closest('#startSelected'))startTrip(selected);
  if(e.target.closest('#finishTrip'))finishTrip(false);if(e.target.closest('#cancelTrip'))finishTrip(true);
});
$('#vehicles').addEventListener('change',async e=>{const el=e.target;if(!el.dataset.vehicle)return;el.disabled=true;try{const settings=JSON.parse(JSON.stringify(state.settings));settings.vehicles[el.dataset.vehicle]=el.value;state=await api('/commute/settings','PUT',{revision:state.revision,settings});renderState();await refreshPlan();}catch(err){toast(err.message);const result=await api('/commute/state').catch(()=>null);if(result)state=result.state;renderState();}});
$('#toCampus').onclick=()=>setDirection('toCampus');$('#toHome').onclick=()=>setDirection('toHome');
$('#timeMode').onchange=()=>{const show=$('#timeMode').value!=='now';$('#when').hidden=!show;if(show)$('#when').value=localInput(new Date(Date.now()+3600000));};
$('#planForm').onsubmit=e=>{e.preventDefault();refreshPlan(true);};$('#settingsForm').onsubmit=saveSettings;
$('#refreshCommute').onclick=()=>{if(state)refreshPlan(true);else load();};
$('#findHome').onclick=()=>findPlace('home');$('#findCampus').onclick=()=>findPlace('campus');
$('#notificationSetup').onclick=enableNotifications;$('#testNotification').onclick=async()=>{try{await api('/commute/notifications/test','POST',{});toast('测试提醒已提交到手机通知通道。请检查手机是否响铃。');}catch(e){toast(e.message);}};
$('#stopSelect').onchange=()=>{liveStop=$('#stopSelect').value;refreshLive();};
$('#pairForm').onsubmit=async e=>{e.preventDefault();try{const result=await api('/pair','POST',{code:$('#pairCode').value,deviceName:'Triton Daily'});localStorage.setItem('codexLanToken',result.token);window.CodexAndroidNotifications?.configure?.(result.token);$('#pairCode').value='';$('#pairDialog').close();await load();}catch(err){$('#pairError').textContent=err.message;}};
$('#today').textContent=fmtDate(new Date())+' · SAN DIEGO';
document.addEventListener('visibilitychange',()=>{if(!document.hidden&&!document.querySelector('dialog[open]'))refreshPlan();else drawVehicles();});
mapInit();load();
window.CodexConsoleOpenThread=id=>{if(id==='commute')return true;location.assign('/?page=threadDetail&threadId='+encodeURIComponent(id));return true;};
// Installed Android shells acknowledge destinations through this legacy route contract.
// "commute" is a local virtual route, never an app-server task ID.
window.openThread=async id=>window.ConsoleNotificationNavigation.legacy(id);
window.refreshCurrentThread=async()=>{};
window.normalizedNavigationState=()=>({page:'threadDetail',threadId:'commute'});
if('serviceWorker' in navigator&&window.isSecureContext)navigator.serviceWorker.register('/commute/sw.js',{scope:'/commute/'}).catch(()=>{});
// Optional agent-facing read access. Uses the same authenticated planner as the page;
// never starts trips, modifies schedules or sends notifications as a side effect.
if(document.modelContext?.registerTool){
  const lifecycle=new AbortController();window.addEventListener('pagehide',()=>lifecycle.abort(),{once:true});
  try{Promise.resolve(document.modelContext.registerTool({
    name:'read_commute_plan',title:'查询 UCSD 通勤方案',
    description:'比较去学校或返回出发地的完整通勤方案。可指定圣地亚哥当地到达时间；只读，不改设置、不开始行程。',
    inputSchema:{type:'object',properties:{direction:{type:'string',enum:['toCampus','toHome']},arrivalTime:{type:'string',description:'可选，YYYY-MM-DDTHH:mm，America/Los_Angeles'}},required:['direction'],additionalProperties:false},
    annotations:{readOnlyHint:true,untrustedContentHint:true},
    async execute(input){
      if(!input||!['toCampus','toHome'].includes(input.direction)||Object.keys(input).some(k=>!['direction','arrivalTime'].includes(k)))throw new Error('Invalid commute query');
      if(input.arrivalTime!==undefined&&!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/.test(input.arrivalTime))throw new Error('Invalid local arrival time');
      const result=await api('/commute/plan','POST',{direction:input.direction,when:input.arrivalTime||null,arriveBy:!!input.arrivalTime});
      return {updatedAt:result.updatedAt,from:result.from.name,to:result.to.name,warnings:result.warnings,options:result.options.map(o=>({mode:o.mode,title:o.title,available:o.available,minutes:o.minutes,leaveAt:o.leaveAt,arriveAt:o.arriveAt,basis:o.basis,recommended:o.id===result.recommendedId}))};
    }
  },{signal:lifecycle.signal})).catch(()=>{});}catch{}
}
