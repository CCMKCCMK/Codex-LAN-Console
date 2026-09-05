(function (root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  else root.CodexRemoteArtifacts = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  function displayName(path) {
    const value = String(path || '').replace(/[\\/]+$/, '');
    return value.split(/[\\/]/).filter(Boolean).pop() || '交付文件';
  }

  function cacheKey(threadId, path) {
    return `${String(threadId || '')}\n${String(path || '')}`;
  }

  function normalizeReferencePath(path) {
    const value = String(path || '');
    return /^\/[A-Za-z]:[\\/]/.test(value) ? value.slice(1) : value;
  }

  function artifactKind(file) {
    const mime = String(file?.mime || file?.contentType || '').toLowerCase();
    const extension = String(file?.name || file?.path || '').split('.').pop()?.toLowerCase();
    if (mime.startsWith('image/') || ['png', 'jpg', 'jpeg', 'gif', 'webp', 'svg', 'bmp', 'heic'].includes(extension)) return 'image';
    if (mime.startsWith('video/') || ['mp4', 'webm', 'mov', 'mkv', 'avi'].includes(extension)) return 'video';
    if (extension === 'apk' || extension === 'aab') return 'android';
    if (extension === 'pdf') return 'pdf';
    if (['cer', 'crt', 'der'].includes(extension) || ['application/pkix-cert', 'application/x-x509-ca-cert'].includes(mime)) return 'certificate';
    if (['zip', '7z', 'rar'].includes(extension)) return 'archive';
    return file?.kind || 'file';
  }

  function escapeMarkdownHtmlPreservingFileLinks(text) {
    const destinations = [];
    let value = String(text || '').replace(/(\]\(\s*)<([^>\r\n]+)>/g, (match, prefix, destination) => {
      const normalized = normalizeReferencePath(destination.trim());
      if (!/^(?:[A-Za-z]:[\\/]|\\\\|\/)/.test(normalized) ||
          !/\.[A-Za-z0-9]{1,8}(?::\d+(?::\d+)?)?$/.test(normalized)) return match;
      const token = `\uE000CODEX_REMOTE_FILE_${destinations.length}\uE001`;
      destinations.push({ token, destination });
      return prefix + token;
    });
    value = value.replace(/</g, '&lt;');
    for (const item of destinations)
      value = value.replace(item.token, `<${item.destination}>`);
    return value;
  }

  function failurePresentation(error, path) {
    const name = displayName(path);
    const status = Number(error?.status) || 0;
    if (status === 403) {
      return {
        title: `${name} 已找到，但电脑端尚未允许读取`,
        detail: '该文件位于任务项目之外。电脑端授权修复后，可以在这里直接重试。'
      };
    }
    if (status === 404) {
      return {
        title: `${name} 暂时无法按这条引用定位`,
        detail: '源文件可能仍在远程电脑的其他项目目录中。修复路径后可以直接重试，无需重新生成。'
      };
    }
    if (status === 413) {
      return {
        title: `${name} 超过可传输大小`,
        detail: '请先在远程电脑上压缩文件，或拆分后再下载。'
      };
    }
    if (status === 401) {
      return {
        title: `${name} 需要重新连接电脑`,
        detail: '当前手机的连接凭据已经失效。重新配对后即可读取。'
      };
    }
    if (!status || status >= 500) {
      return {
        title: `${name} 暂时没有从远程电脑读取成功`,
        detail: '文件不会因此丢失。请稍后重试；系统不会重复创建下载任务。'
      };
    }
    return {
      title: `${name} 暂时无法读取`,
      detail: String(error?.message || '电脑端拒绝了这次文件读取请求。')
    };
  }

  function createSingleFlight() {
    const pending = new Map();
    return {
      run(key, operation) {
        if (pending.has(key)) return pending.get(key);
        const request = Promise.resolve()
          .then(operation)
          .finally(() => pending.delete(key));
        pending.set(key, request);
        return request;
      },
      has(key) {
        return pending.has(key);
      },
      clear(key) {
        pending.delete(key);
      },
      get size() {
        return pending.size;
      }
    };
  }

  return {
    artifactKind,
    cacheKey,
    createSingleFlight,
    displayName,
    escapeMarkdownHtmlPreservingFileLinks,
    failurePresentation,
    normalizeReferencePath
  };
});
