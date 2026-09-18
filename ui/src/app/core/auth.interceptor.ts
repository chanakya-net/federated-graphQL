import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';

import { SessionService } from './session.service';

/** Adds the selected user's JWT to `/graphql` requests only. Every other request is left untouched. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (!isGraphqlUrl(req.url)) return next(req);
  const token = inject(SessionService).token();
  return next(token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req);
};

function isGraphqlUrl(url: string): boolean {
  return url === '/graphql' || url.startsWith('/graphql/') || url.startsWith('/graphql?');
}
