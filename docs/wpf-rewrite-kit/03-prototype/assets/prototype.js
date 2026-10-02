/* 一次性参考原型：内存状态、虚构数据，系统动作全部模拟，不调用真实 Clipboard/任务/存档。 */
(() => {
  const $ = (s) => document.querySelector(s);
  const escape = (s) => String(s).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const icon = (id, n = 16) => `<svg width="${n}" height="${n}" viewBox="0 0 24 24" aria-hidden="true"><use href="#${id}"></use></svg>`;
  const keys = {up:'ArrowUp',down:'ArrowDown',enter:'Enter',escape:'Escape',search:' ',pin:'z',note:'b',delete:'Delete'};
  const labels = {ArrowUp:'↑',ArrowDown:'↓',Enter:'⏎',Escape:'Esc',' ':'空格',z:'Z',b:'B',Delete:'Del'};
  const chip = key => labels[key] ?? key;
  const preferences = {light:{label:'亮色',icon:'i-sun',next:'dark'},dark:{label:'暗色',icon:'i-moon',next:'system'},system:{label:'跟随系统',icon:'i-monitor',next:'light'}};
  const params = new URLSearchParams(location.search);
  const media = matchMedia('(prefers-color-scheme: dark)');
  const state = {entries:[],selected:0,query:'',mode:'浏览态',composing:false,visible:true,theme:preferences[params.get('theme')]?params.get('theme'):'system',system:'auto',shortcut:'Control+Shift+V',autoStart:false,pending:new Map(),note:null,error:null,toasts:[],captureStatus:null,exited:false};
  let searchTimer, scrollTimer, captureTimer, nextToast = 1;
  const now = Date.now();
  function sample() {
    const contents = [
      ['重写的第一条原则：熟悉的交互保持不变。\n文字和图片自动记录，按内容判定条目身份。','notepad','重写需求',true,3],
      ['Ctrl+Shift+V 呼出剪贴板面板，选择后按 Enter 粘贴回原来的输入框。','Code','快捷键说明',true,8],
      ['image','PixPin','界面参考',false,10],
      ['使用 WPF 虚拟化列表，按可视区加载缩略图。\n完整图片留在磁盘，复制时再读取。\n缓存需要字节与数量双上限。\n这一行用于演示三行截断。','Code','性能计划',false,18],
      ['今天的会议记录\n1. 保留旧存档\n2. 验证管理员窗口粘贴\n3. 实测空闲内存','微信','',false,38],
      ['https://learn.microsoft.com/dotnet/desktop/wpf/','msedge','官方文档',false,80],
      ['搜索支持正文、备注、来源应用，多个词使用 AND 匹配，结果保持原顺序。','notepad','搜索规格',false,140],
      ['image','SnippingTool','透明截图示例',false,1500],
      ['完成一项功能，就同步文档、验证失败路径，再提交可回退的版本。','Code','',false,3200],
      ['临时片段：待办事项与常用内容都可以添加备注。','notepad','待办',false,4200],
      ['一段较长的文字用于检查面板宽度约束：即使没有任何空格，very_long_machine_identifier_0123456789_abcdefghijk 也不应该撑开卡片。','Code','',false,5000]
    ];
    return contents.map((a,i) => ({id:i===2?'1cc08a9e-8ec1-4f53-a6c2-a50a72eb5b20':i===7?'94bca8c1-3209-4d84-9502-34a89f7140c4':`demo-${i+1}`,type:a[0]==='image'?'image':'text',text:a[0],source:a[1],title:`${a[1]} - 示例`,exePath:`C:\\Apps\\${a[1]}.exe`,note:a[2],pinned:a[3],pinnedAt:a[3]?now-i:0,ts:now-a[4]*60000}));
  }
  const terms = () => state.query.trim().toLowerCase().split(/\s+/).filter(Boolean);
  function visible() { return state.entries.filter(e => !state.pending.has(e.id) && terms().every(t => [e.type==='text'?e.text:'',e.note,e.source,e.title,e.exePath].join(' ').toLowerCase().includes(t))); }
  function current() { return visible()[state.selected]; }
  function highlight(text) {
    let spans=[{text,hit:false}];
    for (const t of terms()) spans=spans.flatMap(p => {
      if(p.hit) return [p];
      const out=[]; let rest=p.text, at;
      while((at=rest.toLowerCase().indexOf(t))!==-1) {if(at)out.push({text:rest.slice(0,at),hit:false});out.push({text:rest.slice(at,at+t.length),hit:true});rest=rest.slice(at+t.length);}
      if(rest)out.push({text:rest,hit:false});return out;
    });
    return spans.map(p=>p.hit?`<mark>${escape(p.text)}</mark>`:escape(p.text)).join('');
  }
  function time(ts) {const d=Date.now()-ts;return d<60000?'刚刚':d<3600000?`${Math.floor(d/60000)} 分钟前`:d<86400000?`${Math.floor(d/3600000)} 小时前`:d<172800000?'昨天':`${Math.floor(d/86400000)} 天前`;}
  function setEffect(text) {$('#last-effect').textContent=text;}
  function readout() {const e=current();$('#state-readout').textContent=`${state.visible?'可见':'已停靠'} / ${state.mode}${state.composing?' / IME 组合中':''}\n主题偏好：${preferences[state.theme].label}\n生效主题：${document.documentElement.dataset.theme==='dark'?'暗':'亮'}\n选中：${e?.id??'无'}\n待删除：${state.pending.size} / 筛选结果：${visible().length}`;}
  function setTheme(value) {state.theme=value;const dark=state.system==='auto'?media.matches:state.system==='dark';document.documentElement.dataset.theme=value==='system'?(dark?'dark':'light'):value;const c=preferences[value];$('#theme-toggle').innerHTML=icon(c.icon,15);$('#theme-toggle').title=`主题：${c.label}，点击切换为${preferences[c.next].label}`;$('#theme-toggle').setAttribute('aria-label',$('#theme-toggle').title);trayRender();readout();}
  function footer() {
    const count=state.entries.length-state.pending.size;
    const groups = [[keys.up,keys.down,'选择'],[keys.enter,'复制'],[keys.pin,'置顶'],[keys.note,'备注'],[keys.delete,'删除'],[keys.escape,'隐藏']];
    let content;
    if(state.error) content=`<span class="footer-error" role="status">${escape(state.error)}</span>`;
    else if(state.note) content=`<span class="hints"><span><kbd class="kbd">${chip(keys.enter)}</kbd>保存</span><span><kbd class="kbd">${chip(keys.escape)}</kbd>取消</span><span id="note-count">${state.note.draft.length}/200</span></span>`;
    else content=`<span class="hints" aria-hidden="true">${groups.map(g=>`<span><kbd class="kbd">${g.slice(0,-1).map(chip).join('')}</kbd>${g.at(-1)}</span>`).join('')}</span>`;
    $('#footer').innerHTML=`<span id="status-count">${count} 条</span>${content}`;
  }
  function renderCards() {
    const list=visible();state.selected=Math.max(0,Math.min(state.selected,list.length-1));
    $('#cards').hidden=!list.length;$('#empty-state').hidden=!!list.length;
    $('#empty-state').innerHTML=`<div class="empty-state__icon">${icon(state.entries.length?'i-search':'i-layers',20)}</div><p class="empty-state__title">${state.entries.length?'无匹配结果':'还没有剪切板内容'}</p><p class="empty-state__hint">${state.entries.length?'点击 ✕ 按钮可清除搜索。':'去复制一些文字或图片吧，它们会自动出现在这里'}</p>`;
    $('#cards').innerHTML=list.map((e,i)=>`<li class="card${i===state.selected?' is-selected':''}" role="option" aria-selected="${i===state.selected}" data-id="${e.id}" data-selected="${i===state.selected}">${e.type==='image'?`<div class="card__thumb"><img src="assets/sample-image.svg" alt="剪贴板图片（本地演示素材）" draggable="false"></div><div class="card__name">${e.id}.png</div>`:`<div class="card__body">${highlight(e.text)}</div>`}<div class="card__meta"><span class="src">${escape(e.source)}</span><span class="sep">·</span><span>${time(e.ts)}</span>${e.pinned?`<span class="sep">·</span><span class="card__pin" aria-label="已置顶">${icon('i-pin',12)}</span>`:''}${state.note?.id===e.id?`<span class="sep">·</span><input class="note-input" aria-label="此条目的备注" placeholder="添加备注…" maxlength="200" spellcheck="false" value="${escape(state.note.draft)}">`:e.note?`<span class="sep">·</span><span class="card__note" title="${escape(e.note)}">${highlight(e.note)}</span>`:''}</div></li>`).join('');
    footer();readout();requestAnimationFrame(updateScroll);
  }
  function select(i) {state.selected=Math.max(0,Math.min(i,visible().length-1));$('#cards').querySelectorAll('.card').forEach((el,n)=>{el.classList.toggle('is-selected',n===state.selected);el.dataset.selected=String(n===state.selected);el.setAttribute('aria-selected',String(n===state.selected));});$('#cards [data-selected=true]')?.scrollIntoView({block:'nearest',behavior:'instant'});readout();}
  function updateScroll() {const el=$('#cards'),h=el.clientHeight,total=el.scrollHeight;const thumb=Math.max(28,h*h/total);$('.hud-scrollbar-thumb').style.height=`${thumb}px`;$('.hud-scrollbar-thumb').style.transform=`translateY(${total>h?el.scrollTop/(total-h)*(h-thumb):0}px)`;$('.hud-scrollbar').classList.toggle('is-visible',total>h&&!el.hidden);clearTimeout(scrollTimer);scrollTimer=setTimeout(()=>$('.hud-scrollbar').classList.remove('is-visible'),900);}
  function toast(message,error=false,action=null,duration=2600) {const id=nextToast++,el=document.createElement('div');el.className='toast';el.innerHTML=`<span class="toast__icon${error?' toast__icon--error':''}">${icon(error?'i-x':'i-check',14)}</span><span class="toast__msg">${escape(message)}</span>${action?'<button class="toast__action">撤销</button>':''}`;$('#toasts').append(el);state.toasts.push({id,el});if(action)el.querySelector('button').onclick=()=>{action();el.remove();};setTimeout(()=>{el.remove();state.toasts=state.toasts.filter(t=>t.id!==id);},duration);}
  function exitInput() {state.mode='浏览态';state.composing=false;state.query='';clearTimeout(searchTimer);$('#search-input').value='';$('#search-input').readOnly=true;$('#search-input').placeholder='搜索剪贴板…';$('#search-input').blur();$('#search-clear').hidden=true;$('#search-chip').hidden=false;renderCards();}
  function search() {if(state.mode==='捕获态')return;finishNote(false);state.mode='搜索态';state.query='';$('#search-input').value='';$('#search-input').readOnly=false;$('#search-input').placeholder='搜索文字、备注或来源应用…';$('#search-chip').hidden=true;renderCards();$('#search-input').focus();}
  function show() {document.activeElement?.blur();state.exited=false;state.visible=true;state.note=null;state.error=null;exitInput();$('#panel').hidden=false;$('#parked-hint').hidden=true;$('#cards').scrollTop=0;select(0);}
  function park() {finishNote(false);state.visible=false;state.mode='浏览态';$('#capture').hidden=true;clearTimeout(captureTimer);exitInput();$('#panel').hidden=true;$('#parked-hint').hidden=false;readout();}
  function promote(e) {state.entries=state.entries.filter(x=>x.id!==e.id);if(e.pinned){e.pinnedAt=Date.now();state.entries.unshift(e);}else state.entries.splice(state.entries.filter(x=>x.pinned).length,0,e);}
  function pin() {const e=current();if(!e)return;e.pinned=!e.pinned;promote(e);state.selected=visible().findIndex(x=>x.id===e.id);renderCards();select(state.selected);}
  function copy() {const e=current();if(!e)return;promote(e);renderCards();$(`[data-id="${e.id}"]`)?.classList.add('is-copied');if($('#paste-failure').checked){state.error='无法恢复原输入框，已取消本次粘贴，避免写入错误窗口。';footer();toast(state.error,true);setEffect('模拟：剪贴板已写入、落位完成；恢复失败，保持面板可见');}else{setEffect(`模拟：已复制并粘贴 ${e.type==='image'?e.id+'.png':e.source+' 的文字'}，随后停靠`);park();}}
  function remove() {const e=current();if(!e||state.pending.has(e.id))return;const timer=setTimeout(()=>{state.pending.delete(e.id);if($('#delete-failure').checked){toast('删除失败，请重试',true);setEffect('模拟删除提交失败：恢复可见');}else{state.entries=state.entries.filter(x=>x.id!==e.id);setEffect('模拟删除已提交（真实存档未修改）');}renderCards();},6000);state.pending.set(e.id,timer);renderCards();toast('已删除',true,()=>{if(!state.pending.has(e.id))return;clearTimeout(state.pending.get(e.id));state.pending.delete(e.id);renderCards();setEffect('已撤销：移除可见性遮罩');},6000);}
  function editNote() {const e=current();if(!e)return;exitInput();state.mode='备注编辑态';state.note={id:e.id,draft:e.note};renderCards();const input=$('.note-input');input.focus();if(input.value)input.select();input.addEventListener('input',()=>{state.note.draft=input.value;$('#note-count').textContent=`${input.value.length}/200`;});input.addEventListener('blur',()=>finishNote(false),{once:true});}
  function finishNote(cancel) {const n=state.note;if(!n)return;state.note=null;state.mode='浏览态';if(!cancel){const e=state.entries.find(x=>x.id===n.id);if(e)e.note=Array.from(n.draft.trim()).slice(0,200).join('');}renderCards();}
  function capture() {show();state.mode='捕获态';state.captureStatus=null;$('#capture-current').textContent=state.shortcut.replace('Control','Ctrl');$('#capture-status').hidden=true;$('#capture').hidden=false;$('#capture').tabIndex=-1;$('#capture').focus();readout();}
  function endCapture() {clearTimeout(captureTimer);$('#capture').hidden=true;state.mode='浏览态';readout();}
  function captureKey(event) {if(event.key==='Escape'){endCapture();return;}const mods=[];if(event.ctrlKey)mods.push('Control');if(event.altKey)mods.push('Alt');if(event.shiftKey)mods.push('Shift');if(event.metaKey)mods.push('Super');let main=/^Key[A-Z]$/.test(event.code)?event.code.slice(3):/^Digit\d$/.test(event.code)?event.code.slice(5):/^F([1-9]|1\d|2[0-4])$/.test(event.code)?event.code:({Space:'Space',Enter:'Enter',Tab:'Tab',Backspace:'Backspace',Delete:'Delete',Insert:'Insert',Home:'Home',End:'End',PageUp:'PageUp',PageDown:'PageDown',ArrowUp:'Up',ArrowDown:'Down',ArrowLeft:'Left',ArrowRight:'Right'}[event.code]);if(!main)return;const status=$('#capture-status');status.hidden=false;const combo=[...mods,main].join('+');const invalid=!mods.some(m=>m!=='Shift');const occupied=combo==='Control+Alt+X'||main==='F12';status.className=`shortcut-status ${invalid||occupied?'err':'ok'}`;status.textContent=invalid?'请包含 Ctrl / Alt / Win 修饰键':occupied?`${combo} 已被占用或无效，请换一个`:`已设置为 ${combo.replace('Control','Ctrl')}`;if(!invalid&&!occupied){state.shortcut=combo;trayRender();clearTimeout(captureTimer);captureTimer=setTimeout(endCapture,1200);setEffect('快捷键仅在当前演示内更新，未注册系统热键');}}
  function trayRender() {$('#tray-shortcut').textContent=state.shortcut.replace('Control','Ctrl');$('#tray-startup').textContent=`${state.autoStart?'✓ ':''}开机启动`;$('#tray-theme-label').textContent=`主题 ▸ 当前：${preferences[state.theme].label}`;document.querySelectorAll('[data-theme-choice]').forEach(b=>b.textContent=`${b.dataset.themeChoice===state.theme?'✓ ':''}${preferences[b.dataset.themeChoice].label}`);}
  function reset(empty=false) {for(const timer of state.pending.values())clearTimeout(timer);state.pending.clear();state.toasts.forEach(t=>t.el.remove());state.toasts=[];state.entries=empty?[]:sample();show();setEffect('演示数据已重置；没有修改用户存档');}
  $('#cards').addEventListener('click',e=>{if(e.target.closest('input'))return;const card=e.target.closest('.card');if(card)select(visible().findIndex(x=>x.id===card.dataset.id));});
  $('#cards').addEventListener('dblclick',e=>{if(e.target.closest('input'))return;if(e.target.closest('.card'))copy();});
  $('#cards').addEventListener('scroll',updateScroll,{passive:true});
  $('#search-well').addEventListener('click',()=>{if(state.mode!=='搜索态')search();});
  $('#search-input').addEventListener('input',()=>{$('#search-clear').hidden=!$('#search-input').value;clearTimeout(searchTimer);searchTimer=setTimeout(()=>{state.query=$('#search-input').value;state.selected=0;renderCards();select(0);},120);});
  $('#search-input').addEventListener('compositionstart',()=>{state.composing=true;readout();});$('#search-input').addEventListener('compositionend',()=>{state.composing=false;readout();});
  $('#search-clear').onclick=e=>{e.stopPropagation();clearTimeout(searchTimer);state.query='';$('#search-input').value='';$('#search-clear').hidden=true;state.selected=0;renderCards();$('#search-input').focus();};
  $('#theme-toggle').onmousedown=e=>e.preventDefault();$('#theme-toggle').onclick=e=>{e.stopPropagation();setTheme(preferences[state.theme].next);};
  $('#capture-cancel').onclick=endCapture;$('#summon').onclick=()=>state.visible?park():show();$('#tray-open').onclick=()=>{trayRender();$('#tray').hidden=!$('#tray').hidden;};
  $('#tray').onclick=e=>{const b=e.target.closest('button');if(!b)return;if(b.dataset.themeChoice)setTheme(b.dataset.themeChoice);else switch(b.dataset.action){case'show':show();break;case'capture':capture();break;case'startup':state.autoStart=!state.autoStart;trayRender();setEffect('仅模拟开机启动意图，未创建计划任务');break;case'clear':reset(true);break;case'exit':park();state.exited=true;setEffect('模拟退出；重新呼出可继续演示');break;}$('#tray').hidden=true;};
  $('#sample-data').onclick=()=>reset();$('#empty-data').onclick=()=>reset(true);$('#system-theme').onchange=e=>{state.system=e.target.value;setTheme(state.theme);};media.addEventListener('change',()=>setTheme(state.theme));window.addEventListener('resize',updateScroll);
  document.addEventListener('pointerdown',e=>{if(state.visible&&!e.target.closest('#panel')&&!e.target.closest('.demo-tools'))park();});
  document.addEventListener('keydown',e=>{
    if(e.target.closest('.demo-tools'))return;
    if(e.ctrlKey&&e.shiftKey&&e.code==='KeyV'&&state.mode!=='捕获态'){e.preventDefault();state.visible?park():show();return;}
    if(!state.visible)return;
    if(state.mode==='捕获态'){e.preventDefault();if(!e.repeat)captureKey(e);return;}
    if(e.isComposing||state.composing)return;
    if(state.note){if(e.key==='Enter'){e.preventDefault();finishNote(false);}else if(e.key==='Escape'){e.preventDefault();finishNote(true);}return;}
    const k=e.key.length===1?e.key.toLowerCase():e.key;
    if(state.mode==='搜索态'&&![keys.up,keys.down,keys.enter,keys.escape].includes(k))return;
    if(Object.values(keys).includes(k))e.preventDefault();
    if(k===keys.up)select(state.selected-1);else if(k===keys.down)select(state.selected+1);
    else if(!e.repeat){if(k===keys.escape)state.mode==='搜索态'?exitInput():park();else if(k===keys.enter)copy();else if(k===keys.search)search();else if(k===keys.pin)pin();else if(k===keys.note)editNote();else if(k===keys.delete)remove();}
  });
  $('#search-chip').textContent=chip(keys.search);state.entries=sample();setTheme(state.theme);renderCards();
  if(params.get('scene')==='empty')reset(true);if(params.get('scene')==='capture')capture();if(params.get('scene')==='error'){$('#paste-failure').checked=true;copy();}
})();
