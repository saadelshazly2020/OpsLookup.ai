response="""
{
  "CurrentPage": 4596,
  "PagesCount": 6563,
  "PageSize": 2557,
  "RowsCount": 3037,
  "Results": [
    {
      "HasFreeText": false,
      "InquiryReference": "00000000-0000-0000-0000-000000000000",
      "Model": {
        "Code": 7851,
        "AttorneyTypeName": "string",
        "AttorneyPlaceName": "string",
        "AttorneyStatusName": "string",
        "FirstParties": [
          {
            "IdentityNo": "string",
            "FullName": "string",
            "IdentityTypeName": "string",
            "GenderName": "string",
            "NationalityName": "string",
            "BirthDate": "1998-01-23T07:44:41.987Z",
            "BirthDateHijri": "string",
            "PartySefaTypeName": "string"
          }
        ],
        "SecondParties": [
          {
            "IdentityNo": "string",
            "FullName": "string",
            "IdentityTypeName": "string",
            "GenderName": "string",
            "NationalityName": "string",
            "BirthDate": "1998-01-23T07:44:41.987Z",
            "BirthDateHijri": "string",
            "PartySefaTypeName": "string"
          }
        ],
       
       
        "EndDateGreg": "string",
        "IssueDateGreg": "string",
        "IssuerName": "string",
        "IssuerIdentity": "string",
       
      }
    }
  ],
  "ResponseMessage": "string"
}

"""

request="""
{
"request.attorneyCode":"رقم الوكالة",
"request.identityNumber":"الرقم الوطني الموحد",
"request.cRNumber":"رقم السجل التجاري",
"request.status":"حالة الوكالة - رقم",
}

"""
headers="""
{
"SystemID":"معرّف النظام الفريد",
"Authorization":"Bearer token",
"accept":"application/json"
}
"""