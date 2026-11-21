// Shared diagnostics only. Transport and storage stay in the selected adapter.
export function diagnostics(mode) {
  const bus=new EventTarget(), logs=[];
  const api={mode,bus,logs,connection:mode==='mock'?'connected':'offline',
    emit(topic,message,{toast=true,level='ok'}={}) {
      const event={time:new Date().toLocaleTimeString([], {hour12:false}),topic,message,toast,level};
      logs.unshift(event);if(logs.length>120)logs.pop();
      bus.dispatchEvent(new CustomEvent('log',{detail:event}));return event;
    },
    save(topic,message){api.emit(topic,message,{toast:false});},
    setConnection(value){if(api.connection!==value){api.connection=value;bus.dispatchEvent(new Event('connection'));}}
  };
  return api;
}
