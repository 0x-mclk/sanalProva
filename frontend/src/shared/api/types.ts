import type { Schemas } from './client';

// OpenAPI sözleşmesinden (schema.d.ts) türetilen, uygulama genelinde kullanılan tip adları.
export type SessionDetail = Schemas['SessionDetail'];
export type SessionSummary = Schemas['SessionSummary'];
export type SessionStep = Schemas['SessionStep'];
export type PhotoDto = Schemas['PhotoDto'];
export type DraftDto = Schemas['DraftDto'];
export type DraftItemDto = Schemas['DraftItemDto'];
export type GeneratedImageDto = Schemas['GeneratedImageDto'];
export type JobDto = Schemas['JobDto'];
export type OutfitMode = Schemas['OutfitMode'];
export type ProductListItem = Schemas['ProductListItem'];
export type ProductDetail = Schemas['ProductDetail'];
export type VariantDto = Schemas['VariantDto'];
export type CategoryDto = Schemas['CategoryDto'];
export type ImageUrls = Schemas['ImageUrls'];
export type ProductType = Schemas['ProductType'];
export type ConsentNoticeDto = Schemas['ConsentNoticeDto'];
export type MeResponse = Schemas['MeResponse'];
export type StoreAccessDto = Schemas['StoreAccessDto'];
export type AgreementDto = Schemas['AgreementDto'];
export type OrganizationListItem = Schemas['OrganizationListItem'];
export type OrganizationDetail = Schemas['OrganizationDetail'];
export type CreateOrganizationRequest = Schemas['CreateOrganizationRequest'];
export type CreateOrganizationResponse = Schemas['CreateOrganizationResponse'];
export type SetupLinkResponse = Schemas['SetupLinkResponse'];
export type ProductWriteRequest = Schemas['ProductWriteRequest'];
export type CategoryWriteRequest = Schemas['CategoryWriteRequest'];
export type VariantWriteRequest = Schemas['VariantWriteRequest'];

export const isJobActive = (job: Pick<JobDto, 'status'> | undefined) => job?.status === 'Queued' || job?.status === 'Running';
