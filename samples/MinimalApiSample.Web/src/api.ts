import axios from 'axios'
import { instrumentAxios } from '@codeatlas/web'
// Types only: lets Axios accept ix.axiosConfig() (adds `codeatlas` to AxiosRequestConfig).
import type {} from '@codeatlas/web/axios'

/** Paths under the app's base, so the dev-server proxy also works behind code-server's /absproxy/<port>/. */
export const apiPath = (path: string) => `${import.meta.env.BASE_URL}api/${path}`

export const api = axios.create({ baseURL: `${import.meta.env.BASE_URL}api` })
// Requests made with ix.axiosConfig() get trace context; without the CodeAtlas runtime this does nothing.
instrumentAxios(api)

export interface Product {
  sku: string
  name: string
  stock: number
}
